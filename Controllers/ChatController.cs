using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IConfiguration _configuration;

    private readonly HttpClient _httpClient;
    private readonly IHttpClientFactory _httpClientFactory;
    public ChatController(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {

        _configuration = configuration;
        _httpClientFactory = httpClientFactory;

    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] UserRequest request)
    {
        var totalStopwatch = Stopwatch.StartNew();

        string apiKey = _configuration["AzureOpenAI:ApiKey"];
        string endpoint = _configuration["AzureOpenAI:Endpoint"];
        string deploymentName = _configuration["AzureOpenAI:DeploymentName"];
        string apiVersion = "2024-03-01-preview";

        string searchEndpoint = _configuration["AzureOpenAI:SearchEndpoint"];
        string searchKey = _configuration["AzureOpenAI:SearchKey"];
        string indexName = _configuration["AzureOpenAI:IndexName"];

        string url = $"{endpoint}/openai/deployments/{deploymentName}/chat/completions?api-version={apiVersion}";

        bool isHumanAsk = Regex.IsMatch(request.Question, @"\b(speak|talk|connect|chat|need|want|ask)\b.*\b(human|agent|person|representative)\b", RegexOptions.IgnoreCase);

        var payload = new
        {
            messages = new[]
            {
            new {
                role = "system",
                content = @"You are Nomi Support Bot, an intelligent AI assistant.
You retrieve answers from an AI Vector Search database that includes useful content from Nomi web pages.
Always extract and display any visible page URLs found in the source text (e.g., 'Content from: https://...').

Instructions:
- Only include Page Reference URL if it’s a webpage link (e.g., https://www.nomi.co.uk/xyz), not an image (.png, .jpg, etc.).
- If both a web page URL and image URL are present, list them both under Page Reference URL and Image URL separately.
- Do NOT include 'Content from:' or any extra label text.
- Output raw URLs only like: Page Reference URL: https://...

Your response format:
- Answer: [answer here]
- Page Reference URL: [https://...] — only if mentioned in the content
- Image URL: [https://...] — only if available in the source content"
            },
            new { role = "user", content = request.Question }
        },
            temperature = 0.5,
            max_tokens = 1000,
            top_p = 0.9,
            frequency_penalty = 0,
            presence_penalty = 0,
            data_sources = new[]
            {
            new
            {
                type = "azure_search",
                parameters = new
                {
                    endpoint = searchEndpoint,
                    index_name = indexName,
                    semantic_configuration = $"{indexName}-semantic-configuration",
                    query_type = "vector_semantic_hybrid",
                    in_scope = true,
                    strictness = 3,
                    top_n_documents = 10,
                    authentication = new { type = "api_key", key = searchKey },
                    embedding_dependency = new { type = "deployment_name", deployment_name = "text-embedding-3-large" },
                    fields_mapping = new
                    {
                        content_field = "content",
                        title_field = "title",
                        filepath_field = "page_url"
                    }
                }
            }
        }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient("AzureOpenAI");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return BadRequest(errorContent);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var responseJson = JsonDocument.Parse(responseContent);

        var chatMessage = responseJson.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            .Trim();

        chatMessage = chatMessage.Trim();
        string pageReferenceUrl = null;

        // ✅ Extract only the first valid Page Reference URL (exclude images)
        var pageRefMatches = Regex.Matches(chatMessage, @"Page Reference URL:[\s\S]*?-\s*(https?:\/\/[^\s\)<>""']+)", RegexOptions.IgnoreCase);

        var pageRefCandidates = pageRefMatches
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim())
            .Where(u => !Regex.IsMatch(u, @"\.(png|jpg|jpeg|svg|gif|webp)$", RegexOptions.IgnoreCase))
            .Distinct()
            .ToList();

        pageReferenceUrl = pageRefCandidates.FirstOrDefault();


        // === Extract and clean image URLs ===
        var imageUrls = new List<string>();

        foreach (Match match in Regex.Matches(chatMessage, @"!\[.*?\]\((https?:\/\/[^\s\)]+)\)", RegexOptions.IgnoreCase))
            if (Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))
                imageUrls.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(chatMessage, @"\[.*?\]\((https?:\/\/[^\s\)]+)\)", RegexOptions.IgnoreCase))
            if (Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))
                imageUrls.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(chatMessage, @"Image URL:\s*(https?:\/\/\S+)", RegexOptions.IgnoreCase))
            if (Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))
                imageUrls.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(chatMessage, @"\[(https?:\/\/[^\s\]]+)\]", RegexOptions.IgnoreCase))
            if (Uri.IsWellFormedUriString(match.Groups[1].Value, UriKind.Absolute))
                imageUrls.Add(match.Groups[1].Value);

        imageUrls = imageUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // === Clean message ===
        chatMessage = Regex.Replace(chatMessage, @"!\[.*?\]\((https?:\/\/[^\s\)]+)\)", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"\[(https?:\/\/[^\s\]]+)\]", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"Image URL:\s*https?:\/\/\S+", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"Image URL:\s*", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"Page Reference URL:\s*\[.*?\]\((https?:\/\/[^\s\)]+)\)", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"Page Reference URL:\s*(https?:\/\/[^\s\)]+)", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"(<br\s*/?>)?\s*(N/A|Not\s*available\.?)\s*", "", RegexOptions.IgnoreCase);
        //chatMessage = Regex.Replace(chatMessage, @"\((https?:\/\/[^\s\)]+)\)", "", RegexOptions.IgnoreCase);
        //chatMessage = Regex.Replace(chatMessage, @"https?:\/\/[^\s]+", "", RegexOptions.IgnoreCase);

        chatMessage = Regex.Replace(chatMessage, @"Content from:\s*https?:\/\/[^\s<]+", "", RegexOptions.IgnoreCase);
        chatMessage = Regex.Replace(chatMessage, @"Page Reference URL:\s*</br>?\s*Content from:\s*https?:\/\/[^\s<]+", "", RegexOptions.IgnoreCase);


        // Extract main answer
        string firstAnswerOnly = Regex.Match(chatMessage, @"Answer:\s*(.+?)(?=(\nAnswer:|$))", RegexOptions.Singleline)?.Groups[1].Value?.Trim();
        if (!string.IsNullOrEmpty(firstAnswerOnly))
            chatMessage = firstAnswerOnly;

        // Optional: Convert to HTML using markdown parser
        string cleanedReply = CleanCitations(chatMessage);
        string htmlResponse = Markdown.ToHtml(MarkdownToHtml(cleanedReply));
        htmlResponse = Regex.Replace(htmlResponse, @"(\\n|\n|\r|\s)+$", "", RegexOptions.Singleline);

        // Clean empty page reference URLs
        pageReferenceUrl = string.IsNullOrWhiteSpace(pageReferenceUrl) ? null : pageReferenceUrl;

        // Remove blank "Page Reference URL:" blocks from the HTML
        htmlResponse = Regex.Replace(htmlResponse, @"<br\s*/?>\s*Page Reference URL:\s*<br\s*/?>", "", RegexOptions.IgnoreCase);

        // Only add actual pageReferenceUrl if it's valid
        if (!string.IsNullOrEmpty(pageReferenceUrl))
        {
            htmlResponse += $"<br><strong>Page Reference URL:</strong> <a href='{pageReferenceUrl}' target='_blank'>{pageReferenceUrl}</a>";
        }
        // Remove any "Page Reference URL:" lines that are not followed by a valid link
        htmlResponse = Regex.Replace(htmlResponse, @"(<br\s*/?>)?\s*Page Reference URL:\s*(<br\s*/?>)?\s*(</div>)?", "", RegexOptions.IgnoreCase);


        totalStopwatch.Stop();

        return Ok(new
        {
            response = new
            {
                plainText = $"NomiBot: Answer: {chatMessage.Trim()}",
                html = htmlResponse,
                images = imageUrls.Any() ? imageUrls : null,
                IsHumanAsk = isHumanAsk ? "yes" : "no",
                PageReferenceUrl = pageReferenceUrl
            }
        });
    }



    private string CleanCitations(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Remove citation markers like [doc1], [doc23], etc.
        text = Regex.Replace(text, @"\[doc\d+\]", "", RegexOptions.IgnoreCase);

        // Remove "NomiBot: Answer:" prefix if present
        text = text.Replace("NomiBot: Answer:", "").Trim();

        // Remove markdown headers like ###, ##, #
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);

        // Normalize newline characters
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // Collapse more than two newlines into two
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        // Remove all * or ** used for markdown bold/italic
        text = text.Replace("*", "");

        // Normalize multiple spaces
        text = Regex.Replace(text, @"[ \t]{2,}", " ");

        text = Regex.Replace(text, @"^\s*-\s*", "", RegexOptions.Multiline);
        return text;
    }


    private string MarkdownToHtml(string markdown)
    {
        string html = markdown;

        // Bold formatting
        html = Regex.Replace(html, @"\\(.?)\\*", "<label>$1</label>");

        // Line breaks for other content
        html = Regex.Replace(html, @"\n+", "</br>");
        html = Regex.Replace(html, "</li></br>", "</li>");

        // Wrap numbered items in <ol>
        if (html.Contains("<li>"))
        {
            html = $"<ol>{html}</ol>";
        }

        return $"<div>{html}</div>";
    }


    private string AddNumberingToSteps(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (!Regex.IsMatch(text, @"\b(Steps)\b", RegexOptions.IgnoreCase))
            return text;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        int startIndex = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"\b(Steps)\b", RegexOptions.IgnoreCase))
            {
                startIndex = i + 1;
                break;
            }
        }

        if (startIndex == -1 || startIndex >= lines.Length)
            return text;

        var sb = new StringBuilder();

        // Preserve lines before the steps section
        for (int i = 0; i < startIndex; i++)
            sb.AppendLine(lines[i]);

        int stepNumber = 1;

        for (int i = startIndex; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
                continue;
            }

            if (Regex.IsMatch(line, @"^\d+\.\s")) // already numbered
            {
                sb.AppendLine(line);
            }
            else if (IsImageUrlLine(line)) // image URL or line containing image
            {
                sb.AppendLine(line); // keep as-is, no numbering
            }
            else
            {
                string cleanedLine = line.StartsWith("-") ? line.Substring(1).TrimStart() : line;
                sb.AppendLine($"{stepNumber}. {cleanedLine}");
                stepNumber++;
            }
        }

        return sb.ToString().TrimEnd();
    }


    private bool IsImageUrlLine(string line)
    {
        // Matches lines that contain common image URL formats
        return Regex.IsMatch(line, @"(Image URL:\s*)?\[?https?:\/\/[^\]\s]+(\.png|\.jpg|\.jpeg|\.gif|\.webp|\.svg)?\]?", RegexOptions.IgnoreCase);
    }


    public class UserRequest
    {
        public string Question { get; set; }

    }

}
