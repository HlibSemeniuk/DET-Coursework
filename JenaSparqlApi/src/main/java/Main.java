import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ArrayNode;
import com.fasterxml.jackson.databind.node.ObjectNode;
import io.javalin.Javalin;
import org.apache.jena.rdf.model.*;
import org.apache.jena.query.*;
import org.apache.jena.reasoner.*;
import org.apache.jena.reasoner.rulesys.GenericRuleReasoner;
import org.apache.jena.reasoner.rulesys.Rule;
import org.apache.jena.update.*;

import java.io.*;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;
import java.util.stream.Collectors;

public class Main {
    public static void main(String[] args) {
        Javalin app = Javalin.create().start(8081);

        AtomicReference<String> filePath = new AtomicReference<>("");
        app.post("/path", ctx -> {
            filePath.set(ctx.body());
        });


        // SWRL-правила
        String rulesText = """
            [rule_monograph:
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Наукова_Публікація>)
              (?pub <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#кількість_сторінок_на_автора> ?pagesPerAuthor)
              greaterThan(?pagesPerAuthor, 24.99)
              ->
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Монографія>)
            ]
            
            [rule_dissertation:
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Наукова_Публікація>)
              (?pub <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#кількість_сторінок> ?pages)
              (?pub <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#кількість_сторінок_на_автора> ?pagesPerAuthor)
              greaterThan(?pages, 79.99)
              lessThan(?pagesPerAuthor, 24.99)
              ->
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Дисертація>)
            ]
            
            [rule_article:
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Наукова_Публікація>)
              (?pub <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#кількість_сторінок> ?pages)
              (?pub <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#кількість_сторінок_на_автора> ?pagesPerAuthor)
              lessThan(?pages, 79.99)
              greaterThan(?pages, 3.99)
              lessThan(?pagesPerAuthor, 24.99)
              ->
              (?pub rdf:type <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#Стаття>)
            ]
            """;

        // Парсинг правил
        List<Rule> rules = Rule.parseRules(rulesText);
        System.out.println("Вбудовані правила завантажено:");
        rules.forEach(System.out::println);


        // Створення reasoner
        Reasoner reasoner = new GenericRuleReasoner(rules);
        reasoner.setDerivationLogging(true);

        app.post("/query", ctx -> {
            String sparql = ctx.body();

            // Завантаження RDF-моделі
            Model rawModel = ModelFactory.createDefaultModel();
            try (FileInputStream fis = new FileInputStream(filePath.get())) {
                rawModel.read(fis, null, "TTL");
            }

            try {

                // Виконання UPDATE
                Dataset dataset = DatasetFactory.create(rawModel);
                UpdateRequest update = UpdateFactory.create(sparql);
                UpdateAction.execute(update, dataset);

                // Cтворення infModel
                InfModel infModel = ModelFactory.createInfModel(reasoner, rawModel);

                // Об'єднання raw + дедукованих фактів
                Model fullModel = ModelFactory.createUnion(rawModel, infModel.getDeductionsModel());

                // Запис об'єднаної моделі у файл
                try (FileOutputStream out = new FileOutputStream(filePath.get())) {
                    fullModel.write(out, "TTL");
                }

                ctx.result("RDF-файл оновлено та правила застосовані").status(200);

            } catch (Exception e) {
                ctx.status(400).result("Помилка SPARQL-запиту: " + e.getMessage());
            }
        });

        app.post("/selectAll", ctx -> {
            String sparql = ctx.body();

            // Завантаження RDF-моделі
            Model rawModel = ModelFactory.createDefaultModel();
            try (FileInputStream fis = new FileInputStream(filePath.get())) {
                rawModel.read(fis, null, "TTL");
            }

            // Cтворення infModel
            InfModel infModel = ModelFactory.createInfModel(reasoner, rawModel);

            Query query = QueryFactory.create(sparql);
            try (QueryExecution qexec = QueryExecutionFactory.create(query, infModel)) {
                ResultSet rs = qexec.execSelect();

                ObjectMapper mapper = new ObjectMapper();

                // Перетворюємо кожний рядок у JSON-об’єкт
                ArrayNode arr = mapper.createArrayNode();
                while (rs.hasNext()) {
                    QuerySolution sol = rs.next();


                    ObjectNode obj = mapper.createObjectNode();
                    obj.put("Title",      sol.getLiteral("назва").getString());
                    obj.put("Type",       sol.getLiteral("типНазва").getString());
                    obj.put("PublicationDate", sol.getLiteral("дата").getString());
                    obj.put("PageCount",  sol.contains("сторінок") ? sol.getLiteral("сторінок").getInt() : 0);
                    obj.put("AuthorCount",sol.contains("авторів") ? sol.getLiteral("авторів").getInt() : 0);
                    obj.put("PagePerAuthor", sol.contains("сторінокНаАвтора")
                            ? sol.getLiteral("сторінокНаАвтора").getDouble() : 0.0);
                    obj.put("Language",   sol.contains("мова") ? sol.getLiteral("мова").getString() : "");
                    obj.put("Journal",    sol.contains("журналНазва") ? sol.getLiteral("журналНазва").getString() : "");

                    String authorsCsv = sol.contains("Автори") ? sol.getLiteral("Автори").getString() : "";
                    List<String> authors = Arrays.stream(authorsCsv.split("\\s*,\\s*"))
                            .filter(s -> !s.isBlank())
                            .map(String::trim)
                            .collect(Collectors.toList());
                    obj.putPOJO("Authors", authors);

                    String fieldsCsv = sol.contains("Галузі") ? sol.getLiteral("Галузі").getString() : "";
                    List<String> fields = Arrays.stream(fieldsCsv.split("\\s*,\\s*"))
                            .filter(s -> !s.isBlank())
                            .map(String::trim)
                            .collect(Collectors.toList());
                    obj.putPOJO("Fields", fields);

                    String kwsCsv = sol.contains("Ключові_слова") ? sol.getLiteral("Ключові_слова").getString() : "";
                    List<String> keywords = Arrays.stream(kwsCsv.split("\\s*,\\s*"))
                            .filter(s -> !s.isBlank())
                            .map(String::trim)
                            .collect(Collectors.toList());
                    obj.putPOJO("Keywords", keywords);

                    arr.add(obj);
                }

                // Віддаємо JSON-масив
                ctx.json(arr);
            }

        });

        System.out.println("🟢 Сервер працює на http://localhost:8081/query");
    }
}
