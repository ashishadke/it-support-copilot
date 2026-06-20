namespace ITSupport.Api.Services;

// Splits a document into medium-sized, overlapping chunks (same strategy proven
// in the console RAG: fixed size + overlap so ideas spanning a boundary survive).
public static class Chunker
{
    public static List<string> Split(string text, int chunkSize = 600, int overlap = 100)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var chunks = new List<string>();
        if (text.Length == 0) return chunks;

        int start = 0;
        while (start < text.Length)
        {
            int length = Math.Min(chunkSize, text.Length - start);
            chunks.Add(text.Substring(start, length));
            start += chunkSize - overlap;
        }
        return chunks;
    }
}
