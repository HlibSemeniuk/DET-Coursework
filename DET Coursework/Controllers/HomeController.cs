using DET_Coursework.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.StyledXmlParser.Jsoup.Internal;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Query.Datasets;
using VDS.RDF.Update;
using VDS.RDF.Writing;
using static iText.Kernel.Pdf.Colorspace.PdfSpecialCs;

namespace DET_Coursework.Controllers
{
    public class HomeController : Controller
    {
        [HttpGet]
        public IActionResult Index() => View();

        [HttpPost]
        public async Task<IActionResult> Index(IFormFile pdfFile, string ontologyPath)
        {

            if (!string.IsNullOrEmpty(ontologyPath))
            {
                using var httpClient = new HttpClient();
                var content = new StringContent(ontologyPath, Encoding.UTF8, "text/plain");
                var response = await httpClient.PostAsync("http://localhost:8081/path", content);

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                }
                else
                {
                    string error = await response.Content.ReadAsStringAsync();
                    return StatusCode((int)response.StatusCode, $"Java server error: {response.StatusCode}. Details: {error}");
                }
            }


            if (pdfFile?.Length > 0 && pdfFile.ContentType == "application/pdf")
            {
                using var ms = new MemoryStream();
                pdfFile.CopyTo(ms);
                var fileBytes = ms.ToArray();
                var info = ExtractPublicationInfo(fileBytes);
                ViewBag.PdfBytes = fileBytes;
                return View("Result", info);
            }
            ModelState.AddModelError("", "���� �����, ���������� ������ PDF-����.");

           ViewData["OntologyPath"] = ontologyPath;

            return View();
        }


        [HttpPost]
        public IActionResult Add(PublicationInfo info, string PdfBytesBase64)
        {

            AddToOntology(info);

            ViewBag.Message = "������ ����� � ��������";
            ViewBag.PdfBytes = Convert.FromBase64String(PdfBytesBase64);
            return View("Result", info);
        }

        private async void AddToOntology(PublicationInfo info)
        {
            string insertQuery = BuildInsertQuery(info);

            //�������� ���������
            using var client = new HttpClient();
            var content = new StringContent(insertQuery, Encoding.UTF8, "text/plain");

            try
            {
                var response = await client.PostAsync("http://localhost:8081/query", content);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    throw new Exception($"Java server error: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {error}");
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Java server response: " + responseBody);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception occurred: " + ex.Message);
                // ����� ������ ��� ��� ��������
                throw;
            }
        }

        private string BuildInsertQuery(PublicationInfo info)
        {
            string pagePerAuthorString = info.PagePerAuthor.ToString(CultureInfo.InvariantCulture);

            var builder = new StringBuilder($@"
            PREFIX onto: <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2#>
            PREFIX inst: <http://www.semanticweb.org/user/ontologies/2025/1/untitled-ontology-2/individuals/>
            PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
            PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>

            INSERT DATA {{
              inst:�������_���������_{info.Title.Replace(" ", "_").Replace("'", "").Replace(", ", "").Replace(",", "")}
                rdf:type onto:�������_��������� ;
                onto:����� ""{info.Title}"" ;
                onto:����_��������� ""{info.PublicationDate.ToString("yyyy-MM-ddTHH:mm:ss")}""^^xsd:dateTime ;
                onto:�������_������� {info.PageCount} ;
                onto:�������_������ {info.AuthorCount} ;
                onto:�������_�������_��_������ ""{pagePerAuthorString}""^^xsd:double ;
                onto:���� ""{info.Language}"" ;
                onto:�������� {string.Join(" , ", info.Authors.Select(name => $"inst:���������_{name.Replace(" ", "_").Replace("'", "")}"))} ;
                onto:�����������_� inst:������_{info.Journal.Replace(" ", "_").Replace("'", "").Replace(", ", "").Replace(",", "")} ;
                onto:��������_��_�����_����� {string.Join(" , ", info.Fields.Select(field => $"onto:������_{field.Replace(" ", "_").Replace("'", "")}"))} ;
                onto:������_�������_����� {string.Join(" , ", info.Keywords.Select(keyword => $"inst:�������_�����_{keyword.Replace(" ", "_").Replace("'", "")}"))} .

                inst:������_{info.Journal.Replace(" ", "_").Replace("'", "").Replace(", ", "").Replace(",", "")}
                    rdf:type onto:������ ;
                    onto:����� ""{info.Journal}"" .
            
            ");

            foreach (var author in info.Authors)
            {
                builder.AppendLine($@"
                    inst:���������_{author.Replace(" ", "_").Replace("'", "")}
                        rdf:type onto:��������� ;
                        onto:ϲ� ""{author}"" .
                
                ");
            }

            foreach (var field in info.Fields)
            {
                builder.AppendLine($@"
                    onto:������_{field.Replace(" ", "_").Replace("'", "")}
                        rdf:type onto:������_����� ;
                        onto:����� ""{field}"" .
                
                ");
            }

            foreach (var keyword in info.Keywords)
            {
                builder.AppendLine($@"
                    inst:�������_�����_{keyword.Replace(" ", "_").Replace("'", "")}
                        rdf:type onto:�������_c���� ;
                        onto:����� ""{keyword}"" .
                
                ");
            }

            builder.AppendLine("}");

            string insertQuery = builder.ToString();

           return insertQuery;
        }

        private PublicationInfo ExtractPublicationInfo(byte[] fileBytes)
        {
            using var reader = new PdfReader(new MemoryStream(fileBytes));
            using var pdfDoc = new PdfDocument(reader);

            int pageCount = pdfDoc.GetNumberOfPages();

            var sb = new StringBuilder();
            for (int i = 1; i <= pageCount; i++)
            {
                var strategy = new SimpleTextExtractionStrategy();
                sb.AppendLine(PdfTextExtractor.GetTextFromPage(pdfDoc.GetPage(i), strategy));
            }
            string allText = sb.ToString();
            allText = Regex.Replace(allText, @"\r?\n", " ");

            var lines = allText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            string title = FindTitle(allText);

            List<string> authors = FindAuthors(allText, title);

            DateTime publicationDate = FindPublicationDate(allText);

            string journalName = FindJournalName(allText, title);

            string language = DetermineLanguage(allText);

            string firstAuthorName = authors.FirstOrDefault();
            List<string> keywords = FindKeywords(allText, firstAuthorName);

            List<string> fields = DetermineFields(journalName);


            var publication = new PublicationInfo();

            publication.Title = title.Replace('�', '\'');
            publication.Authors = authors
                .Select(a => a.Replace('�', '\''))
                .ToList();

            publication.PublicationDate = publicationDate;

            publication.Journal = journalName.Replace('�', '\'');
            publication.Language = language;
            publication.PageCount = pageCount;
            publication.Keywords = keywords
                .Select(k => k.Replace('�', '\''))
                .ToList();

            publication.Fields = fields
                .Select(f => f.Replace('�', '\''))
                .ToList();
            
            int authorCount = authors.Count;
            double pagePerAuthor = pageCount / (double)authorCount;
            pagePerAuthor = Math.Round(pagePerAuthor, 1);

            publication.AuthorCount = authorCount;
            publication.PagePerAuthor = pagePerAuthor;

            if (pageCount >= 90)
            {
                publication.Type = "����������";
            }
            else if (pagePerAuthor >= 25)
            {
                publication.Type = "����������";
            }
            else if ((double)pageCount > 3)
            {
                publication.Type = "������";
            }
            else
            {
                publication.Type = "����_�����������";
            }
            
            return publication;
        }

        private string FindTitle(string text)
        {
            string title = "";
            string titlePattern = @"(?<=\(\d{4}\)\.\s)(?<title>[\s\S]+?)(?=\.\s*(?:\r?\n)?[A-Z])";
            Match match = Regex.Match(text, titlePattern);
            string rawTitle = match.Groups["title"].Value;

            title = Regex.Replace(rawTitle, @"\s+", " ").Trim();
            return title;
        }

        private List<string> FindAuthors(string text, string title)
        {
            List<string> citationNames = new List<string>();

            string citationNamesPattern = @"(?<=Cite as:\s*)(?<authors>.+?)(?=\s*\(\d{4}\)\.)";
            Match match = Regex.Match(text, citationNamesPattern);
            
            string citationNamesLine = match.Groups["authors"].Value;

            string personPattern = @"[A-Za-z'-]+,\s*(?:[A-Z]\.\s*)+";

            var rx = new Regex(personPattern);
            citationNames = rx.Matches(citationNamesLine)
                               .Cast<Match>()
                               .Select(m => m.Value.Trim().TrimEnd(','))
                               .ToList();

            // ����� ��� ��������� ������� ������� ��� ������� ������
            int[] nameCountForPerson = citationNames
                .Select(name => name.Count(c => c == '.'))
                .ToArray();

            List<string> surnames = citationNames
                .Select(name => name.Split(',')[0].Trim())
                .ToList();

            string regexTitle = title
                .ToUpper()
                .Replace(" ", @"\s+");

            string fullNamePattern = $@"[\s\S]*?{regexTitle}\s*[\s\S]*?";
            int n = 0;

            // ������ ������ ��� ������� ������
            foreach (var name in surnames)
            {
                fullNamePattern += $@"(?<authors{n}>";

                for (int j = 0; j < nameCountForPerson[n]; j++)
                {
                    fullNamePattern += @$"[A-Za-z'-]+\s+";
                }

                fullNamePattern += $@"{name})[\s\S]*?";
                n++;
            }

            match = Regex.Match(text, fullNamePattern);

            if (!match.Success)
            {
                title = title.Replace("- ", "");
                regexTitle = title
                .ToUpper()
                .Replace(" ", @"\s+");

                fullNamePattern = $@"[\s\S]*?{regexTitle}\s*[\s\S]*?";
                n = 0;

                foreach (var name in surnames)
                {
                    fullNamePattern += $@"(?<authors{n}>";

                    for (int j = 0; j < nameCountForPerson[n]; j++)
                    {
                        fullNamePattern += @$"[A-Za-z'-]+\s+";
                    }

                    fullNamePattern += $@"{name})[\s\S]*?";
                    n++;
                }

                match = Regex.Match(text, fullNamePattern);
            }

            List<string> authorsFullNames = new List<string>();

            for (int i = 0; i < nameCountForPerson.Length; i++)
            {
                string fullName = match.Groups[$"authors{i}"].Value;
                authorsFullNames.Add(fullName);
            }

            return authorsFullNames;
        }

        private DateTime FindPublicationDate(string text)
        {
            string dateRegex = @"(?:Accepted)\s*[:\-�]\s*(\w+\s+\d{1,2},?\s+\d{4})";

            Match match = Regex.Match(text, dateRegex);

            string date = match.Groups[1].Value.Trim();
            DateTime publicationDate = DateTime.Parse(date);

            return publicationDate;
        }

        private string FindJournalName(string text, string title)
        {
            string journalPattern = @$"(?:{title.Replace(" ", @"\s+") + '.'})\s*[\s\S]*?(?<journal>(.*?)),\s*\d+";

            Match match = Regex.Match(text, journalPattern);
            string journalName = match.Groups["journal"].Value;

            return journalName;
        }

        private string DetermineLanguage(string text)
        {
            if (Regex.IsMatch(text, @"\b���������", RegexOptions.IgnoreCase))
                return "���������";
            else
                return "English";
        }

        private List<string> FindKeywords(string text, string firstAuthorName)
        {
            var names = firstAuthorName.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries).ToList();

            // ������������ ������ ���� � ������ ��� ������������ � ����������� �����
            var namesPattern = string.Join("|", names.Select(Regex.Escape));

            string keywordsPattern = @$"(?:Keywords\s+)(?<keywords>.*?)(?=\s+(INTRODUCTION|{namesPattern})\b)";
            
            Match match = Regex.Match(text, keywordsPattern);
            string keywordsLine = match.Groups["keywords"].Value;

            List<string> keywords = keywordsLine
                .Split(new string[] { ", ", "," }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .ToList();

            return keywords;
        }

        private List<string> DetermineFields(string journalName)
        {
            Dictionary<string, string> JournalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Informing Science: The International Journal of an Emerging Transdiscipline", "IT, ̳������������ ����������" },
                { "Journal of Information Technology Education: Research", "IT �����, ���������" },
                { "Journal of Information Technology Education: Innovations in Practice", "IT �����, ��������� � ����" },
                { "Journal of Information Technology Education: Discussion Cases", "IT �����, ���������� ������������� ������" },
                { "Interdisciplinary Journal of e-Skills and Lifelong Learning", "�����, IT" },
                { "Interdisciplinary Journal of Information, Knowledge, and Management", "IT, ����������, ��������� ��������" },
                { "International Journal of Doctoral Studies", "���� �����, IT, ���������� ���������" },
                { "Issues in Informing Science and Information Technology", "IT, ����������� �������, �����" },
                { "Journal for the Study of Postsecondary and Tertiary Education", "���� �����, ���������" },
                { "Informing Faculty", "���� �����, ���������" }
            };

            string fieldsLine = "";

            if (!JournalFields.TryGetValue(journalName.Trim(), out fieldsLine))
            {
                fieldsLine = "������� ������";
            }

            List<string> fields = fieldsLine
               .Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
               .ToList();

            return fields;
        }


    }
}
