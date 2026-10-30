using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using ChatBi.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.Controllers.Semantic;

/// <summary>语义表管理端点。读 semantic:read；写 semantic:manage（DataSteward/SysAdmin）。</summary>
[Route("api/semantic/tables")]
public class SemanticTablesController : SemanticControllerBase
{
    private readonly SemanticTableService _service;

    public SemanticTablesController(SemanticTableService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<SemanticTableResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<SemanticTableResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticTableResponse>> Create([FromBody] SemanticTableRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticTableResponse>> Update(
        long id, [FromBody] SemanticTableRequest request, CancellationToken cancellationToken)
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

/// <summary>语义列管理端点（列不软删，DELETE 为物理删除）。</summary>
[Route("api/semantic/columns")]
public class SemanticColumnsController : SemanticControllerBase
{
    private readonly SemanticColumnService _service;

    public SemanticColumnsController(SemanticColumnService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<SemanticColumnResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<SemanticColumnResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticColumnResponse>> Create([FromBody] SemanticColumnRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.SemanticManage)]
    public async Task<ActionResult<SemanticColumnResponse>> Update(
        long id, [FromBody] SemanticColumnRequest request, CancellationToken cancellationToken)
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
