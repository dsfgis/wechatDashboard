using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using WechatDashboard.Application.Weekly;
using WechatDashboard.Infrastructure.Weekly;

namespace WechatDashboard.Tests;

/// <summary>真实周报模板导出测试：只操作临时副本，并核对模板结构及所有业务条目。</summary>
public static class WeeklyReportExporterTests
{
    private const string TemplatePath =
        @"D:\work\00_部门文档\周报月报\周报\10月周报\【智能化管线管理系统】项目周报20261031.xlsx";

    /// <summary>导出超过固定行数的草稿，验证原文件、合并样式、批注、VML和数据验证均不受影响。</summary>
    public static Task ExportsAllItemsIntoPreservedTemplateCopyAsync()
    {
        AssertTrue(File.Exists(TemplatePath), $"Real weekly report template was not found: {TemplatePath}");
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "WechatDashboard.Tests",
            $"weekly-report-{Guid.NewGuid():N}.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var templateHashBefore = HashFile(TemplatePath);
        var reportDate = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.FromHours(8));
        var draft = CreateDraft(reportDate);

        // 导出器必须自行复制模板，测试不会预先复制或修改用户指定的原文件。
        new OpenXmlWeeklyReportExporter().Export(draft, TemplatePath, outputPath);
        AssertTrue(File.Exists(outputPath), "The exporter should create the selected output workbook.");
        AssertEqual(templateHashBefore, HashFile(TemplatePath), "The source template hash must remain unchanged.");

        using var original = SpreadsheetDocument.Open(TemplatePath, false);
        using var generated = SpreadsheetDocument.Open(outputPath, false);
        var originalWorkbook = original.WorkbookPart!;
        var generatedWorkbook = generated.WorkbookPart!;
        var originalSheet = originalWorkbook.WorksheetParts.Single();
        var generatedSheet = generatedWorkbook.WorksheetParts.Single();

        // 固定模板对象必须保留，不能通过重建工作表换取写入便利。
        AssertEqual(
            originalSheet.Worksheet.Elements<MergeCells>().Single().Count!.Value,
            generatedSheet.Worksheet.Elements<MergeCells>().Single().Count!.Value,
            "Merged-cell ranges should be preserved.");
        AssertEqual(
            originalSheet.Worksheet.Elements<DataValidations>().Single().Count!.Value,
            generatedSheet.Worksheet.Elements<DataValidations>().Single().Count!.Value,
            "Issue and risk status validations should be preserved.");
        AssertEqual(
            originalSheet.WorksheetCommentsPart?.Comments?.Count() ?? 0,
            generatedSheet.WorksheetCommentsPart?.Comments?.Count() ?? 0,
            "Template comments should be preserved.");
        AssertEqual(
            originalSheet.GetPartsOfType<VmlDrawingPart>().Count(),
            generatedSheet.GetPartsOfType<VmlDrawingPart>().Count(),
            "VML comment drawings should be preserved.");
        AssertEqual(
            originalSheet.Worksheet.Elements<Row>().Count(),
            generatedSheet.Worksheet.Elements<Row>().Count(),
            "The exporter must not insert template rows.");
        AssertEqual(
            GetCell(originalSheet, "C11").StyleIndex?.Value,
            GetCell(generatedSheet, "C11").StyleIndex?.Value,
            "Existing business-cell styles should survive content replacement.");

        // 固定项目元数据保留，动态项目名称、周期和报告日期由本次草稿更新。
        AssertEqual(ReadCellText(originalWorkbook, "J3"), ReadCellText(generatedWorkbook, "J3"), "Project manager metadata should be retained.");
        AssertEqual(ReadCellText(originalWorkbook, "F4"), ReadCellText(generatedWorkbook, "F4"), "Reporter metadata should be retained.");
        AssertEqual(ReadCellText(originalWorkbook, "C5"), ReadCellText(generatedWorkbook, "C5"), "Project stage metadata should be retained.");
        AssertEqual("智能化管线管理系统", ReadCellText(generatedWorkbook, "F3"), "The current project name should be exported.");

        // 旧业务内容若存在必须清除，不能与新周报混在同一个单元格。
        var oldFirstWork = ReadCellText(originalWorkbook, "C11");
        var generatedText = generatedSheet.Worksheet.InnerText;
        if (!string.IsNullOrWhiteSpace(oldFirstWork) && oldFirstWork != "工作项-01")
        {
            AssertFalse(generatedText.Contains(oldFirstWork, StringComparison.Ordinal), "Previous-week business content should be cleared.");
        }

        // 每个唯一业务标记恰好出现一次；超出行数的条目应在单元格内换行合并而不是丢弃。
        foreach (var token in AllBusinessTokens(draft))
        {
            AssertEqual(1, CountOccurrences(generatedText, token), $"Business entry should be exported exactly once: {token}");
        }

        // 有依据的数字使用Excel数值单元格；没有依据的下一行进度与工时保持空白。
        AssertEqual(0.8, ReadNumber(generatedSheet, "H11"), 0.0001, "Expected progress should be numeric.");
        AssertEqual(0.6, ReadNumber(generatedSheet, "I11"), 0.0001, "Current progress should be numeric.");
        AssertEqual(10, ReadNumber(generatedSheet, "J11"), 0.0001, "Planned hours should be numeric.");
        AssertEqual(8, ReadNumber(generatedSheet, "K11"), 0.0001, "Actual hours should be numeric.");
        foreach (var reference in new[] { "H12", "I12", "J12", "K12" })
        {
            AssertTrue(string.IsNullOrWhiteSpace(GetCell(generatedSheet, reference).CellValue?.Text), $"Unknown value should stay blank: {reference}");
        }

        var reportDateNumber = ReadNumber(generatedSheet, "J4");
        AssertEqual(
            reportDate.Date,
            DateTime.FromOADate(reportDateNumber).Date,
            "The report date should be written as an Excel date value.");
        return Task.CompletedTask;
    }

    /// <summary>构造覆盖所有模板区域的草稿，并故意超过每个固定条目区域行数。</summary>
    private static WeeklyReportDraft CreateDraft(DateTimeOffset reportDate)
    {
        var metadata = new WeeklyReportMetadata("智能化管线管理系统", reportDate.AddDays(-7), reportDate.AddDays(-1), reportDate);
        var workItems = Enumerable.Range(1, 7).Select(index => new WeeklyWorkItem(
            $"工作项-{index:00}",
            new[] { "沈佳奇" },
            index == 1 ? 0.8 : null,
            index == 1 ? 0.6 : null,
            index == 1 ? 10 : null,
            index == 1 ? 8 : null,
            new[] { (long)index },
            Array.Empty<long>())).ToArray();
        var issues = Enumerable.Range(1, 6).Select(index => new WeeklyIssue(
            $"问题-{index:00}",
            $"解决方案-{index:00}",
            new[] { "张欣宇" },
            "打开",
            new[] { (long)index },
            Array.Empty<long>())).ToArray();
        var risks = Enumerable.Range(1, 6).Select(index => new WeeklyRisk(
            $"风险-{index:00}",
            $"缓解措施-{index:00}",
            new[] { "张彬" },
            "监控中",
            new[] { (long)index },
            Array.Empty<long>())).ToArray();
        var plans = Enumerable.Range(1, 5).Select(index => new WeeklyPlan(
            $"计划-{index:00}",
            $"期望结果-{index:00}",
            new[] { "赵岑东" },
            null,
            new[] { (long)index },
            Array.Empty<long>())).ToArray();
        return new WeeklyReportDraft(
            metadata,
            "总体进展测试",
            workItems,
            issues,
            risks,
            "下周目标测试",
            plans);
    }

    /// <summary>列出所有必须映射的条目及方案/结果文本，供“无遗漏且仅一次”断言使用。</summary>
    private static IEnumerable<string> AllBusinessTokens(WeeklyReportDraft draft)
    {
        foreach (var item in draft.WorkItems) yield return item.Description;
        foreach (var item in draft.Issues)
        {
            yield return item.Description;
            yield return item.Solution;
        }
        foreach (var item in draft.Risks)
        {
            yield return item.Description;
            yield return item.Mitigation;
        }
        foreach (var item in draft.Plans)
        {
            yield return item.Task;
            yield return item.ExpectedResult;
        }
    }

    private static Cell GetCell(WorksheetPart worksheet, string reference) =>
        worksheet.Worksheet.Descendants<Cell>().Single(cell => cell.CellReference?.Value == reference);

    /// <summary>读取文本时同时支持模板已有的共享字符串和导出器写入的内联字符串。</summary>
    private static string ReadCellText(WorkbookPart workbook, string reference)
    {
        var worksheet = workbook.WorksheetParts.Single();
        var cell = GetCell(worksheet, reference);
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.InnerText, out var index))
        {
            return workbook.SharedStringTablePart!.SharedStringTable.ElementAt(index).InnerText;
        }
        return cell.InnerText;
    }

    private static double ReadNumber(WorksheetPart worksheet, string reference)
    {
        var text = GetCell(worksheet, reference).CellValue?.Text;
        AssertTrue(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value), $"Cell {reference} should contain a number.");
        return value;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}");
        }
    }

    private static void AssertEqual(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}");
        }
    }
}
