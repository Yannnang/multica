using System.Net;
using ChatBi.Domain.Entities;
using ChatBi.Domain.Enums;
using ChatBi.Domain.Security;
using FluentAssertions;
using Xunit;

namespace ChatBi.Tests.Integration;

/// <summary>
/// 分页稳定性与 N+1 防护：列表查询固定 2 条 SQL（COUNT + 分页页），指标附带 TableIds 也仅 3 条。
/// SQL 计数经 EF LogTo(CommandExecuted) 在测试宿主内统计。
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SemanticPagingNoNPlusOneTests : SemanticEndpointTestBase
{
    public SemanticPagingNoNPlusOneTests(ChatBiWebApplicationFactory factory) : base(factory)
    {
    }

    private async Task SeedSynonymsAsync(int count)
    {
        var dataSourceId = await SeedDataSourceAsync();
        var tableId = await SeedTableAsync(dataSourceId);
        await using var db = Factory.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            db.Synonyms.Add(new Synonym
            {
                Term = $"N1_{Guid.NewGuid():N}"[..20],
                TargetType = SynonymTargetType.SemanticTable,
                TargetId = tableId,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedMetricsWithTablesAsync(int count)
    {
        var dataSourceId = await SeedDataSourceAsync();
        var tableA = await SeedTableAsync(dataSourceId);
        var tableB = await SeedTableAsync(dataSourceId);
        await using var db = Factory.CreateDbContext();
        var metrics = new List<Metric>();
        for (var i = 0; i < count; i++)
        {
            var metric = new Metric
            {
                Code = $"M_{Guid.NewGuid():N}"[..12],
                DisplayName = "N+1 指标",
                Expression = "SUM({t}.amount)",
                AggregationType = AggregationType.Sum,
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
            };
            metrics.Add(metric);
            db.Metrics.Add(metric);
        }

        await db.SaveChangesAsync(); // 先取 Metric.Id，再补关联表（MetricTable 无导航属性）。

        foreach (var metric in metrics)
        {
            db.MetricTables.Add(new MetricTable { MetricId = metric.Id, SemanticTableId = tableA });
            db.MetricTables.Add(new MetricTable { MetricId = metric.Id, SemanticTableId = tableB });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SynonymList_FixedSqlCount_NoNPlusOne()
    {
        await SeedSynonymsAsync(30);
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);

        Factory.ResetSqlCommandCount();
        var response = await ClientWith(reader).GetAsync("/api/semantic/synonyms?page=1&pageSize=100");
        var sqlCount = Factory.SqlCommandCount;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // 1 条认证查用户 + 1 条 COUNT + 1 条分页查询；若按行 N+1 会是 30+ 条。
        sqlCount.Should().BeLessThanOrEqualTo(3, "列表 = 认证 + COUNT + 分页共 3 条，30 行数据不应触发 N+1");
    }

    [Fact]
    public async Task MetricList_WithTableIds_FixedSqlCount_NoNPlusOne()
    {
        await SeedMetricsWithTablesAsync(15);
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);

        Factory.ResetSqlCommandCount();
        var response = await ClientWith(reader).GetAsync("/api/semantic/metrics?page=1&pageSize=100");
        var sqlCount = Factory.SqlCommandCount;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await JsonAsync(response);
        json["items"]!.AsArray().Should().HaveCountGreaterThanOrEqualTo(15);
        // 1 条认证查用户 + COUNT + 分页 + 1 条批量取 TableIds；15 指标 × 2 关联不触发 N+1（否则 30+ 条）。
        sqlCount.Should().BeLessThanOrEqualTo(4, "TableIds 经 1 条批量查询取回，不按指标逐个查询");
    }

    [Fact]
    public async Task MetricList_IncludesTableIds_ForEveryItem()
    {
        // 共享库：只断言本次铺的指标（按 Code 识别），不假设库中只有这些指标。
        var dataSourceId = await SeedDataSourceAsync();
        var tableA = await SeedTableAsync(dataSourceId);
        var tableB = await SeedTableAsync(dataSourceId);
        var codes = new List<string>();
        await using (var db = Factory.CreateDbContext())
        {
            for (var i = 0; i < 3; i++)
            {
                var code = $"M_{Guid.NewGuid():N}"[..12];
                codes.Add(code);
                var metric = new Metric
                {
                    Code = code,
                    DisplayName = "N+1 指标",
                    Expression = "SUM({t}.amount)",
                    AggregationType = AggregationType.Sum,
                    IsEnabled = true,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                db.Metrics.Add(metric);
                db.SaveChanges();
                db.MetricTables.Add(new MetricTable { MetricId = metric.Id, SemanticTableId = tableA });
                db.MetricTables.Add(new MetricTable { MetricId = metric.Id, SemanticTableId = tableB });
            }

            db.SaveChanges();
        }

        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var json = await JsonAsync(await ClientWith(reader).GetAsync("/api/semantic/metrics?page=1&pageSize=100"));

        var mine = json["items"]!.AsArray()
            .Where(item => codes.Contains(item!.AsObject()["code"]!.GetValue<string>()))
            .ToList();
        mine.Should().HaveCount(3);
        mine.Should().OnlyContain(item => item!.AsObject()["tableIds"]!.AsArray().Count == 2);
    }

    [Fact]
    public async Task Paging_StableAcrossPages_ByAscendingId()
    {
        await SeedSynonymsAsync(25);
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);

        var page1 = await JsonAsync(await ClientWith(reader).GetAsync("/api/semantic/synonyms?page=1&pageSize=10"));
        var page2 = await JsonAsync(await ClientWith(reader).GetAsync("/api/semantic/synonyms?page=2&pageSize=10"));

        var ids1 = page1["items"]!.AsArray().Select(x => (long)x!["id"]!).ToList();
        var ids2 = page2["items"]!.AsArray().Select(x => (long)x!["id"]!).ToList();
        ids1.Should().BeInAscendingOrder();
        ids2.Should().BeInAscendingOrder();
        ids1.Intersect(ids2).Should().BeEmpty("相邻两页不应重叠");
        ids2.All(id => id > ids1.Max()).Should().BeTrue("第 2 页严格延续第 1 页的 Id 排序");
    }

    [Fact]
    public async Task Paging_DefaultsApplied_WhenAbsent()
    {
        var reader = await CreateUserTokenAsync(RoleCodes.BusinessUser);
        var json = await JsonAsync(await ClientWith(reader).GetAsync("/api/semantic/datasources"));

        ((int)json["page"]!).Should().Be(1);
        ((int)json["pageSize"]!).Should().Be(20);
    }
}
