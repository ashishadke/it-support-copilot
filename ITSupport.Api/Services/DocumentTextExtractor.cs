using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

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

    // Reconstruct the VISUAL reading order instead of trusting the PDF's raw stream
    // order. We group words into lines by their vertical position (so a right-aligned
    // date stays on the same line as its role title), order lines top-to-bottom, and
    // order words within a line left-to-right. This keeps "Role, Company ... Dates"
    // intact — which raw page.Text scrambles for multi-column resume layouts.
    private static string ExtractPdf(Stream stream)
    {
        using PdfDocument doc = PdfDocument.Open(stream);
        var sb = new StringBuilder();

        foreach (var page in doc.GetPages())
        {
            // PDF origin is bottom-left, so a higher Y = higher up the page.
            var words = page.GetWords()
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ToList();

            const double yTolerance = 4.0;   // words within ~4pt of each other = same line
            var lines = new List<List<Word>>();

            foreach (var w in words)
            {
                var line = lines.LastOrDefault();
                if (line != null && Math.Abs(line[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= yTolerance)
                    line.Add(w);
                else
                    lines.Add(new List<Word> { w });
            }

            foreach (var line in lines)
                sb.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));

            sb.AppendLine();   // blank line between pages
        }

        return sb.ToString();
    }
}
