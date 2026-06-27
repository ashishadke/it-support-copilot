namespace ITSupport.Api.Services;

// Structure-aware chunking: we split on LINE boundaries and pack whole lines into
// chunks up to a size budget — never cutting mid-word or mid-line. This keeps a
// section's heading + its details together (e.g. a job's "Role, Company, Dates" and
// its bullets), which fixed-size character windows used to slice apart.
public static class Chunker
{
    public static List<string> Split(string text, int maxChars = 700, int overlapLines = 1)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var chunks = new List<string>();
        if (lines.Count == 0) return chunks;

        var current = new List<string>();
        int currentLen = 0;

        foreach (var line in lines)
        {
            // If adding this line would overflow the budget, close the current chunk first.
            if (currentLen + line.Length > maxChars && current.Count > 0)
            {
                chunks.Add(string.Join("\n", current));
                // Carry the last few lines into the next chunk so context spanning a
                // boundary survives (the structure-aware equivalent of overlap).
                current = current.Skip(Math.Max(0, current.Count - overlapLines)).ToList();
                currentLen = current.Sum(l => l.Length);
            }

            current.Add(line);
            currentLen += line.Length;
        }

        if (current.Count > 0)
            chunks.Add(string.Join("\n", current));

        return chunks;
    }
}
