using ITSupport.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ITSupport.Api.Controllers;

// Document ingestion + listing. Uploaded files are chunked, embedded, and stored in pgvector.
[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private readonly RagService _rag;
    private readonly NpgsqlDataSource _db;

    public DocumentsController(RagService rag, NpgsqlDataSource db)
    {
        _rag = rag;
        _db = db;
    }

    // POST /api/documents/upload — upload a PDF or TXT, ingest it into pgvector.
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file uploaded.");

        string text;
        await using (var stream = file.OpenReadStream())
            text = DocumentTextExtractor.Extract(stream, file.FileName);

        int chunks = await _rag.IngestAsync(file.FileName, text);
        return Ok(new { file = file.FileName, characters = text.Length, chunksStored = chunks });
    }

    // GET /api/documents — list which files have been ingested (and chunk counts).
    [HttpGet]
    public async Task<IActionResult> List()
    {
        await using var cmd = _db.CreateCommand(
            "SELECT file_name, COUNT(*) FROM doc_chunks GROUP BY file_name ORDER BY file_name;");
        var docs = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            docs.Add(new { file = reader.GetString(0), chunks = reader.GetInt64(1) });
        return Ok(docs);
    }
}
