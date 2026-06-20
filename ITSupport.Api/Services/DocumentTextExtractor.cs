using UglyToad.PdfPig;

namespace ITSupport.Api.Services;

// Turns an uploaded file (PDF or TXT) stream into plain text for chunking.
public static class DocumentTextExtractor
{
    public static string Extract(Stream fileStream, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => ExtractPdf(fileStream),
            ".txt" => new StreamReader(fileStream).ReadToEnd(),
            _ => throw new NotSupportedException($"Unsupported file type '{ext}'. Use .pdf or .txt.")
        };
    }

    private static string ExtractPdf(Stream stream)
    {
        using PdfDocument doc = PdfDocument.Open(stream);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }
}
