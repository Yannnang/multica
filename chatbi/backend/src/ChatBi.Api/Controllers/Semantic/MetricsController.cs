using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using ChatBi.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.Controllers.Semantic;

/// <summary>
/// 指标管理端点（含 MetricTables 关联：请求体 TableIds 整体替换）。
/// Expression 白名单校验失败 → 422 SEM_EXPRESSION_INVALID。
/// </summary>
[Route("api/semantic/metrics")]
public class MetricsController : SemanticControllerBase
{
    private readonly MetricService _service;

    public MetricsController(MetricService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<MetricResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<MetricResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<MetricResponse>> Create([FromBody] MetricRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<MetricResponse>> Update(
        long id, [FromBody] MetricRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _service.UpdateAsync(id, request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>软删 → 204。</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>表关联管理端点。OnConditionsJson 只收结构化列 id 对（结构/归属校验失败 → 422 SEM_CONDITIONS_INVALID）。</summary>
[Route("api/semantic/relations")]
public class SemanticRelationsController : SemanticControllerBase
{
    private readonly SemanticRelationService _service;

    public SemanticRelationsController(SemanticRelationService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<SemanticRelationResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<SemanticRelationResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticRelationResponse>> Create([FromBody] SemanticRelationRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticRelationResponse>> Update(
        long id, [FromBody] SemanticRelationRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _service.UpdateAsync(id, request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

/// <summary>同义词管理端点（无并发令牌；PUT 为幂等覆盖；DELETE 物理删除）。</summary>
[Route("api/semantic/synonyms")]
public class SynonymsController : SemanticControllerBase
{
    private readonly SynonymService _service;

    public SynonymsController(SynonymService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<SynonymResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<SynonymResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SynonymResponse>> Create([FromBody] SynonymRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SynonymResponse>> Update(
        long id, [FromBody] SynonymRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _service.UpdateAsync(id, request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
