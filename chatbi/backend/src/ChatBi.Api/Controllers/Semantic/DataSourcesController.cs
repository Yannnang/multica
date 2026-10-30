using ChatBi.Application.Common;
using ChatBi.Application.Semantic;
using ChatBi.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatBi.Api.Controllers.Semantic;

/// <summary>
/// 数据源管理端点。读：semantic:read（所有已认证角色）；写：datasource:manage（仅 SysAdmin）。
/// CredentialRef 只收引用名；疑似明文连接串/密码 → 422 SEM_PLAINTEXT_CREDENTIAL，且不落日志。
/// </summary>
[Route("api/semantic/datasources")]
public class DataSourcesController : SemanticControllerBase
{
    private readonly DataSourceService _service;

    public DataSourcesController(DataSourceService service)
    {
        _service = service;
    }

    /// <summary>GET /api/semantic/datasources?page&amp;pageSize → PagedResult（OrderBy Id，稳定排序）。</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<PagedResult<DataSourceResponse>>> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        return Ok(await _service.ListAsync(page, pageSize, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>GET /api/semantic/datasources/{id} → 单查，404 SEM_NOT_FOUND。</summary>
    [HttpGet("{id:long}")]
    [Authorize(Policy = Permissions.SemanticRead)]
    public async Task<ActionResult<DataSourceResponse>> GetById(long id, CancellationToken cancellationToken)
    {
        return Ok(await _service.GetByIdAsync(id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>POST → 201（Location 指向单查）。</summary>
    [HttpPost]
    [Authorize(Policy = Permissions.DataSourceManage)]
    public async Task<ActionResult<DataSourceResponse>> Create([FromBody] DataSourceRequest request, CancellationToken cancellationToken)
    {
        var created = await _service.CreateAsync(request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>PUT /{id}：全量更新，RowVersion 并发令牌，冲突 → 409 SEM_CONCURRENCY_CONFLICT。</summary>
    [HttpPut("{id:long}")]
    [Authorize(Policy = Permissions.DataSourceManage)]
    public async Task<ActionResult<DataSourceResponse>> Update(
        long id, [FromBody] DataSourceRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _service.UpdateAsync(id, request, GetCurrentUserId(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>DELETE /{id}：软删 → 204。</summary>
    [HttpDelete("{id:long}")]
    [Authorize(Policy = Permissions.DataSourceManage)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, GetCurrentUserId(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
