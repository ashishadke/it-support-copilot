using ITSupport.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ITSupport.Api.Controllers;

// Runs the evaluation set and returns a scored report.
[ApiController]
[Route("api/eval")]
public class EvalController : ControllerBase
{
    private readonly EvaluationService _eval;

    public EvalController(EvaluationService eval) => _eval = eval;

    // GET /api/eval — run every test case and return pass/fail + scores.
    [HttpGet]
    public async Task<IActionResult> Run()
    {
        var report = await _eval.RunAsync();
        return Ok(report);
    }
}
