using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Linq;
using DET_Coursework.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Components.Forms;
using static iText.Kernel.Pdf.Colorspace.PdfSpecialCs;
using System.Text.RegularExpressions;
using System.Xml;
using iText.StyledXmlParser.Jsoup.Internal;

namespace DET_Coursework.Controllers
{
    public class HomeController : Controller
    {
        [HttpGet]
        public IActionResult Index() => View();

        [HttpPost]
        public IActionResult Index(IFormFile pdfFile)
        {
            if (pdfFile?.Length > 0 && pdfFile.ContentType == "application/pdf")
            {
                using var ms = new MemoryStream();
                pdfFile.CopyTo(ms);
                var fileBytes = ms.ToArray();
                var info = ExtractPublicationInfo(fileBytes);
                ViewBag.PdfBytes = fileBytes;
                return View("Result", info);
            }
            ModelState.AddModelError("", "Будь ласка, завантажте дійсний PDF-файл.");
            return View();
        }


        [HttpPost]
        public IActionResult Refine(PublicationInfo info, string PdfBytesBase64)
        {
            var fileBytes = Convert.FromBase64String(PdfBytesBase64);
            var refined = ExtractPublicationInfo(fileBytes);
            ViewBag.PdfBytes = fileBytes;
            return View("Result", refined);
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

            List<string> keywords = FindKeywords(allText);

            List<string> fields = DetermineFields(journalName);


            var publication = new PublicationInfo();

            publication.Title = title;
            publication.Authors = authors;
            publication.PublicationDate = publicationDate;
            publication.Journal = journalName;
            publication.Language = language;
            publication.PageCount = pageCount;
            publication.Keywords = keywords;
            publication.Fields = fields;
            publication.Type = "Стаття";

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

            // Масив для зберігання кількості ініціалів для кожного автора
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

            // Додаємо шаблон для кожного автора
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
            string dateRegex = @"(?:Accepted)\s*[:\-–]\s*(\w+\s+\d{1,2},?\s+\d{4})";

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
            if (Regex.IsMatch(text, @"\bАннотація", RegexOptions.IgnoreCase))
                return "Українська";
            else
                return "English";
        }

        private List<string> FindKeywords(string text)
        {
            string keywordsPattern = @"(?:Keywords\s+)(?<keywords>.*?)\s\s+";
            
            Match match = Regex.Match(text, keywordsPattern);
            string keywordsLine = match.Groups["keywords"].Value;

            List<string> keywords = keywordsLine
                .Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            return keywords;
        }

        private List<string> DetermineFields(string journalName)
        {
            Dictionary<string, string> JournalFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Informing Science: The International Journal of an Emerging Transdiscipline", "IT, Міждисциплінарні дослідження" },
                { "Journal of Information Technology Education: Research", "IT Освіта, Педагогіка" },
                { "Journal of Information Technology Education: Innovations in Practice", "IT Освіта, Інновації в освіті" },
                { "Journal of Information Technology Education: Discussion Cases", "IT Освіта, Менеджмент інформаційних систем" },
                { "Interdisciplinary Journal of e-Skills and Lifelong Learning", "Освіта, IT" },
                { "Interdisciplinary Journal of Information, Knowledge, and Management", "IT, Менеджмент, Управління знаннями" },
                { "International Journal of Doctoral Studies", "Вища освіта, IT, Методологія досліджень" },
                { "Issues in Informing Science and Information Technology", "IT, Інформаційні системи, Освіта" },
                { "Journal for the Study of Postsecondary and Tertiary Education", "Вища освіта, Педагогіка" },
                { "Informing Faculty", "Вища освіта, Педагогіка" }
            };

            string fieldsLine = "";

            if (!JournalFields.TryGetValue(journalName.Trim(), out fieldsLine))
            {
                fieldsLine = "Невідома галузь";
            }

            List<string> fields = fieldsLine
               .Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
               .ToList();

            return fields;
        }


    }
}
