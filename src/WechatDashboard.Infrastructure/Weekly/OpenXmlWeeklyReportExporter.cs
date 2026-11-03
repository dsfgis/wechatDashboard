using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using WechatDashboard.Application.Weekly;

namespace WechatDashboard.Infrastructure.Weekly;

/// <summary>
/// 基于 Open XML 的项目周报导出器。
/// 所有修改都发生在模板副本中，不重建工作簿，从而保留模板样式、批注、VML 和数据验证。
/// </summary>
public sealed class OpenXmlWeeklyReportExporter : IWeeklyReportExporter
{
    // 模板名称和容量是固定契约，不通过插入行列来扩展模板。
    private const string RequiredSheetName = "项目周报";
    private const int FixedItemRowCount = 5;
    private const int PlanRowCount = 4;

    // 所有需要写入或依赖的合并区域都预先校验，避免把内容写到错误坐标。
    private static readonly string[] RequiredMergedRanges = BuildRequiredMergedRanges();

    // 元数据区域只覆盖项目名称、统计周期、报告日期和本周总结。
    // 项目编号没有草稿来源，因此与经理、报告人、阶段一样保持原值。
    // 本周工作使用第十一至十五行，不触碰第十行的栏目标题。
    // 问题和风险各自编号，两个区域不会共享序号或状态。
    // 下周目标写入第二十八行开始的既有大合并区域。
    // 下周安排使用第三十三至三十六行，容量与其他业务区域不同。
    // 所有多行合并区域都只定位左上角，其余合并子单元格不动。
    // 上述坐标同时构成模板兼容性契约，布局不符时宁可终止也不误写。

    /// <summary>
    /// 将结构化草稿写入模板副本，并在全部成功后替换输出文件。
    /// 输出路径与模板路径相同会直接报错，从入口杜绝覆盖原模板。
    /// </summary>
    public void Export(WeeklyReportDraft draft, string templatePath, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // 先转为绝对路径再比较，防止相对路径绕过模板保护。
        var templateFullPath = Path.GetFullPath(templatePath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (PathsEqual(templateFullPath, outputFullPath))
        {
            throw new ArgumentException("输出路径不得与模板路径相同。", nameof(outputPath));
        }

        if (!File.Exists(templateFullPath))
        {
            throw new FileNotFoundException("找不到周报模板文件。", templateFullPath);
        }

        // 临时文件必须位于输出目录，才能在同卷内安全替换目标文件。
        var outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new ArgumentException("输出路径必须包含有效目录。", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputFullPath)}.{Guid.NewGuid():N}.tmp");

        // 导出事务以临时副本为唯一写入对象，模板始终以只读语义参与。
        // 随机文件名避免并发导出共用副本，也避免覆盖目录中的既有文件。
        // 复制成功后才打开 Open XML 包，减少无效模板造成的工作簿句柄占用。
        // 工作表校验、内容映射和保存任一步失败都不会进入替换阶段。
        // 目标已存在时使用同卷替换，目标不存在时直接移动完整副本。
        // finally 同时覆盖校验异常、保存异常和替换异常三条失败路径。
        // 因而不存在把半份工作簿暴露为正式输出的正常执行路径。
        try
        {
            // 只复制模板，后续 Open XML 操作始终针对临时副本。
            File.Copy(templateFullPath, temporaryPath, overwrite: false);

            // 复制可能带来只读属性；仅清理副本属性，绝不修改模板属性。
            File.SetAttributes(temporaryPath, FileAttributes.Normal);
            WriteDraft(temporaryPath, draft);

            // 已完成并关闭工作簿后才替换输出，失败时旧输出仍保持不变。
            ReplaceOutput(temporaryPath, outputFullPath);
        }
        finally
        {
            // 成功时临时文件已被移动；失败时在这里清理残留副本。
            if (File.Exists(temporaryPath))
            {
                File.SetAttributes(temporaryPath, FileAttributes.Normal);
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>在临时副本中定位唯一工作表，校验模板后写入全部草稿内容。</summary>
    private static void WriteDraft(string temporaryPath, WeeklyReportDraft draft)
    {
        using var document = SpreadsheetDocument.Open(temporaryPath, isEditable: true);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("模板缺少工作簿主部件。");

        // 模板必须恰好包含一个指定名称的工作表，拒绝猜测工作表顺序。
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray()
            ?? throw new InvalidDataException("模板缺少工作表集合。");
        if (sheets.Length != 1 || sheets[0].Name?.Value != RequiredSheetName)
        {
            throw new InvalidDataException($"模板必须只包含一个名为“{RequiredSheetName}”的工作表。");
        }

        var relationshipId = sheets[0].Id?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId)
            || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
        {
            throw new InvalidDataException("无法定位“项目周报”工作表部件。");
        }

        var worksheet = worksheetPart.Worksheet;
        ValidateMergedRanges(worksheet);

        // 元数据、总结与各业务区域分别写入，避免跨区块清理模板内容。
        WriteMetadata(worksheet, draft.Metadata);
        WriteText(worksheet, "C6", draft.Summary);
        WriteWorkItems(worksheet, draft.WorkItems);
        WriteIssues(worksheet, draft.Issues);
        WriteRisks(worksheet, draft.Risks);
        WriteText(worksheet, "C28", draft.NextWeekGoal);
        WritePlans(worksheet, draft.Plans);

        // 只保存已修改的工作表，其余包部件由 SDK 原样保留。
        worksheet.Save();
    }

    /// <summary>更新可变的项目元数据；经理、报告人、阶段和项目编号保持模板原值。</summary>
    private static void WriteMetadata(Worksheet worksheet, WeeklyReportMetadata metadata)
    {
        WriteText(worksheet, "F3", metadata.ProjectName);

        // 周期是包含两个日期的文本，单日报告日期则使用真正的 Excel 日期数值。
        var period = string.Concat(
            metadata.From.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            "-",
            metadata.To.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        WriteText(worksheet, "C4", period);
        WriteNumber(worksheet, "J4", metadata.ReportDate.Date.ToOADate());

        // 草稿没有阶段进度和总体进度字段，不能沿用上期数据或猜测数值。
        ClearCell(GetRequiredCell(worksheet, "F5"));
        ClearCell(GetRequiredCell(worksheet, "J5"));
    }

    /// <summary>写入本周工作项，并在写入前清除五个固定行中的旧内容。</summary>
    private static void WriteWorkItems(Worksheet worksheet, IReadOnlyList<WeeklyWorkItem> items)
    {
        ClearRows(worksheet, 11, FixedItemRowCount, "C", "G", "H", "I", "J", "K");

        // 每个单元格只使用当前行条目；最后一组可能包含多个溢出条目。
        WriteFixedRows(items, 11, (row, rowItems) =>
        {
            WriteText(worksheet, $"C{row}", JoinLines(rowItems.Select(item => item.Description)));
            WriteText(worksheet, $"G{row}", JoinLines(rowItems.Select(item => JoinOwners(item.Owners))));

            // 进度值保持 0 到 1 的数值，模板百分比样式负责显示。
            WriteNumberSeries(worksheet, $"H{row}", rowItems.Select(item => item.ExpectedProgress).ToArray(), isPercentage: true);
            WriteNumberSeries(worksheet, $"I{row}", rowItems.Select(item => item.CurrentProgress).ToArray(), isPercentage: true);

            // 工时没有明确数据时写空单元格，绝不补零。
            WriteNumberSeries(worksheet, $"J{row}", rowItems.Select(item => item.PlannedHours).ToArray(), isPercentage: false);
            WriteNumberSeries(worksheet, $"K{row}", rowItems.Select(item => item.ActualHours).ToArray(), isPercentage: false);
        });
    }

    /// <summary>写入问题列表，编号按草稿顺序从 1 连续生成。</summary>
    private static void WriteIssues(Worksheet worksheet, IReadOnlyList<WeeklyIssue> items)
    {
        ClearRows(worksheet, 17, FixedItemRowCount, "C", "D", "F", "J", "K");
        WriteFixedRows(items, 17, (row, rowItems) =>
        {
            var firstNumber = row - 17 + 1;
            WriteSequentialNumbers(worksheet, $"C{row}", firstNumber, rowItems.Count);
            WriteText(worksheet, $"D{row}", JoinLines(rowItems.Select(item => item.Description)));
            WriteText(worksheet, $"F{row}", JoinLines(rowItems.Select(item => item.Solution)));
            WriteText(worksheet, $"J{row}", JoinLines(rowItems.Select(item => JoinOwners(item.Owners))));
            WriteText(worksheet, $"K{row}", JoinLines(rowItems.Select(item => item.Status)));
        });
    }

    /// <summary>写入风险列表，采用与问题列表相同的固定行和溢出规则。</summary>
    private static void WriteRisks(Worksheet worksheet, IReadOnlyList<WeeklyRisk> items)
    {
        ClearRows(worksheet, 23, FixedItemRowCount, "C", "D", "F", "J", "K");
        WriteFixedRows(items, 23, (row, rowItems) =>
        {
            var firstNumber = row - 23 + 1;
            WriteSequentialNumbers(worksheet, $"C{row}", firstNumber, rowItems.Count);
            WriteText(worksheet, $"D{row}", JoinLines(rowItems.Select(item => item.Description)));
            WriteText(worksheet, $"F{row}", JoinLines(rowItems.Select(item => item.Mitigation)));
            WriteText(worksheet, $"J{row}", JoinLines(rowItems.Select(item => JoinOwners(item.Owners))));
            WriteText(worksheet, $"K{row}", JoinLines(rowItems.Select(item => item.Status)));
        });
    }

    /// <summary>写入下周安排，计划工时同样遵循无数据即为空的约定。</summary>
    private static void WritePlans(Worksheet worksheet, IReadOnlyList<WeeklyPlan> items)
    {
        ClearRows(worksheet, 33, PlanRowCount, "C", "G", "J", "K");
        WriteFixedRows(items, 33, (row, rowItems) =>
        {
            WriteText(worksheet, $"C{row}", JoinLines(rowItems.Select(item => item.Task)));
            WriteText(worksheet, $"G{row}", JoinLines(rowItems.Select(item => item.ExpectedResult)));
            WriteText(worksheet, $"J{row}", JoinLines(rowItems.Select(item => JoinOwners(item.Owners))));
            WriteNumberSeries(worksheet, $"K{row}", rowItems.Select(item => item.PlannedHours).ToArray(), isPercentage: false);
        }, PlanRowCount);
    }

    /// <summary>
    /// 把任意数量条目映射到固定行：前面的条目各占一行，其余全部按顺序进入最后一行。
    /// 条目数不超过容量时仍逐项逐行映射，不添加空占位内容。
    /// </summary>
    // 以七条工作项为例，前四条分别落到第一至第四固定行。
    // 第五、第六、第七条组成最后一组，共享第五固定行的各列。
    // 对六条下周计划而言，第四至第六条会进入第四固定行。
    // 每组内部仍使用草稿原始顺序，不按责任人、编号或文本重新排序。
    // 调用方收到的行号决定目标坐标，组内容决定该行的全部列值。
    // 文本列按条目换行，数字列也保留同样的行对应关系。
    // 条目为空时不会执行回调，但调用方已提前清空旧业务内容。
    // 整个算法只返回既有行号，因此结构上不可能插入新行。
    private static void WriteFixedRows<T>(
        IReadOnlyList<T> items,
        int firstRow,
        Action<int, IReadOnlyList<T>> writeRow,
        int rowCapacity = FixedItemRowCount)
    {
        var usedRowCount = Math.Min(items.Count, rowCapacity);
        for (var rowIndex = 0; rowIndex < usedRowCount; rowIndex++)
        {
            // 最后一行使用 Skip 收齐剩余条目，因此不会插行，也不会丢弃溢出内容。
            IReadOnlyList<T> rowItems = rowIndex == rowCapacity - 1
                ? items.Skip(rowIndex).ToArray()
                : new[] { items[rowIndex] };
            writeRow(firstRow + rowIndex, rowItems);
        }
    }

    /// <summary>清空一组固定业务行中的指定列，防止较短草稿留下上期数据。</summary>
    private static void ClearRows(Worksheet worksheet, int firstRow, int rowCapacity, params string[] columns)
    {
        for (var rowOffset = 0; rowOffset < rowCapacity; rowOffset++)
        {
            var row = firstRow + rowOffset;
            foreach (var column in columns)
            {
                ClearCell(GetRequiredCell(worksheet, $"{column}{row}"));
            }
        }
    }

    /// <summary>
    /// 写入一组可空数字。单值保留 Excel 数值类型；多值为了逐行对应而使用换行文本。
    /// 多值单元格无法同时承载多个 Excel 数字，百分比会显式带上百分号，未知值保留空行。
    /// </summary>
    // 普通固定行只有单条数据，数值单元格可直接参与 Excel 计算。
    // 百分比仍写入零到一的小数，不预先乘一百，避免模板格式重复缩放。
    // 工时按小时原值写入，不把 null 当成零小时或推测实际投入。
    // 最后一行包含多条数据时，单个 XML 单元格不能保存数值数组。
    // 此时每个已知值占一行，未知值用空行维持与其他列的纵向对应。
    // 多值百分比显式附带百分号，避免 InlineString 绕过百分比样式后误解。
    // 如果整列都没有任何已知值，则连占位换行也不写，直接保持空白。
    // 这种区别既保留常规行的 Excel 类型，也保证溢出条目不丢失。
    private static void WriteNumberSeries(
        Worksheet worksheet,
        string reference,
        IReadOnlyList<double?> values,
        bool isPercentage)
    {
        if (values.Count == 1)
        {
            WriteNumber(worksheet, reference, values[0]);
            return;
        }

        // 全部未知时真正清空，而不是写入一串没有意义的换行。
        if (values.All(value => !value.HasValue))
        {
            ClearCell(GetRequiredCell(worksheet, reference));
            return;
        }

        WriteText(worksheet, reference, JoinLines(values.Select(value => FormatNumber(value, isPercentage))));
    }

    /// <summary>问题或风险编号在单行内为数值，溢出时按行写成连续编号文本。</summary>
    private static void WriteSequentialNumbers(
        Worksheet worksheet,
        string reference,
        int firstNumber,
        int count)
    {
        if (count == 1)
        {
            WriteNumber(worksheet, reference, firstNumber);
            return;
        }

        var numbers = Enumerable.Range(firstNumber, count)
            .Select(number => number.ToString(CultureInfo.InvariantCulture));
        WriteText(worksheet, reference, JoinLines(numbers));
    }

    /// <summary>把可空数字格式化为溢出单元格中的一行，空值返回空字符串。</summary>
    private static string FormatNumber(double? value, bool isPercentage)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }

        EnsureFinite(value.Value);
        return isPercentage
            ? value.Value.ToString("0.##%", CultureInfo.InvariantCulture)
            : value.Value.ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <summary>写入 InlineString 文本，空文本会转换为真正的空单元格。</summary>
    private static void WriteText(Worksheet worksheet, string reference, string? value)
    {
        var cell = GetRequiredCell(worksheet, reference);
        ClearCell(cell);
        var text = SanitizeText(value);
        if (text.Length == 0)
        {
            return;
        }

        // Space=Preserve 确保换行、缩进和首尾空格在 Excel 中完整保留。
        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(new Text(text)
        {
            Space = SpaceProcessingModeValues.Preserve
        });
    }

    /// <summary>写入 Excel 数值；null 表示未知并清空，非有限浮点数拒绝导出。</summary>
    private static void WriteNumber(Worksheet worksheet, string reference, double? value)
    {
        var cell = GetRequiredCell(worksheet, reference);
        ClearCell(cell);
        if (!value.HasValue)
        {
            return;
        }

        EnsureFinite(value.Value);

        // G17 使用不变区域格式并保留双精度往返能力，避免逗号小数分隔符。
        cell.DataType = CellValues.Number;
        cell.CellValue = new CellValue(value.Value.ToString("G17", CultureInfo.InvariantCulture));
    }

    /// <summary>只清理单元格内容和公式，样式、批注锚点及其他模板结构保持不变。</summary>
    private static void ClearCell(Cell cell)
    {
        cell.DataType = null;
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
    }

    /// <summary>
    /// 清理 XML 1.0 不接受的控制字符，并统一换行为 LF。
    /// 制表符和换行属于合法字符会保留，代理项由 Rune 安全处理。
    /// </summary>
    // 模型素材可能来自邮件或聊天正文，不能假定其中没有隐藏控制符。
    // 空字符、纵向制表符等非法码点若直接写入会导致整个工作簿无法解析。
    // Windows 回车换行先归一为 LF，确保多值合并后的行边界一致。
    // 合法的换行和制表符不会删除，因为它们可能承载原文格式信息。
    // 按 Rune 遍历而不是逐 char 删除，可避免拆散合法的非 BMP 字符。
    // 清理只作用于新写入文本，不反向修改共享字符串或模板批注。
    // 清理结果为空时由调用方写成空白单元格，不留下空 InlineString 节点。
    private static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var codePoint = rune.Value;
            var isValidXmlCharacter = codePoint is 0x09 or 0x0A or 0x0D
                || codePoint >= 0x20 && codePoint <= 0xD7FF
                || codePoint >= 0xE000 && codePoint <= 0xFFFD
                || codePoint >= 0x10000 && codePoint <= 0x10FFFF;
            if (isValidXmlCharacter)
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>按单元格坐标定位模板现有单元格，缺失时视为模板不兼容。</summary>
    private static Cell GetRequiredCell(Worksheet worksheet, string reference)
    {
        return worksheet.Descendants<Cell>().FirstOrDefault(cell =>
                   string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException($"模板缺少必需单元格 {reference}。");
    }

    /// <summary>校验全部业务写入区域，任何关键合并缺失都停止导出。</summary>
    private static void ValidateMergedRanges(Worksheet worksheet)
    {
        var actualRanges = worksheet.GetFirstChild<MergeCells>()?
            .Elements<MergeCell>()
            .Select(cell => cell.Reference?.Value)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var missingRanges = RequiredMergedRanges.Where(range => !actualRanges.Contains(range)).ToArray();
        if (missingRanges.Length > 0)
        {
            throw new InvalidDataException($"模板缺少关键合并区域：{string.Join("、", missingRanges)}。");
        }
    }

    /// <summary>构造模板固定合并区域清单，覆盖元数据、总结和四类业务条目。</summary>
    private static string[] BuildRequiredMergedRanges()
    {
        var ranges = new List<string>
        {
            "C3:D3", "F3:G3", "J3:K3",
            "C4:D4", "F4:G4", "J4:K4",
            "C5:D5", "F5:G5", "J5:K5",
            "C6:K9", "C28:K31"
        };

        // 本周工作内容每行的 C:F 都是合并区域。
        for (var row = 11; row <= 15; row++)
        {
            ranges.Add($"C{row}:F{row}");
        }

        // 问题和风险具有相同结构，描述为 D:E，方案或措施为 F:I。
        for (var row = 17; row <= 21; row++)
        {
            ranges.Add($"D{row}:E{row}");
            ranges.Add($"F{row}:I{row}");
        }

        for (var row = 23; row <= 27; row++)
        {
            ranges.Add($"D{row}:E{row}");
            ranges.Add($"F{row}:I{row}");
        }

        // 下周安排的任务为 C:F，期望结果为 G:I。
        for (var row = 33; row <= 36; row++)
        {
            ranges.Add($"C{row}:F{row}");
            ranges.Add($"G{row}:I{row}");
        }

        return ranges.ToArray();
    }

    /// <summary>以同目录临时文件替换输出；目标不存在时直接移动。</summary>
    // 进入此方法前 Open XML 文档已经释放，目标不会引用未刷新的包内容。
    // 同目录策略保证 File.Replace 和 File.Move 不需要跨卷复制半成品。
    // 已存在的输出通过 Replace 交换，用户另行指定的备份策略不由本类接管。
    // 不创建额外备份文件，避免导出目录残留旧周报或泄露业务内容。
    // 新输出直接 Move，移动成功后临时路径自然消失。
    // 若替换因占用或权限失败，异常继续向上传递并触发临时文件清理。
    // 原输出在替换失败时仍是先前完整版本，不会被本方法预先删除。
    private static void ReplaceOutput(string temporaryPath, string outputFullPath)
    {
        if (File.Exists(outputFullPath))
        {
            File.Replace(temporaryPath, outputFullPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporaryPath, outputFullPath);
        }
    }

    /// <summary>Windows 文件路径不区分大小写，比较前同时移除末尾目录分隔符。</summary>
    private static bool PathsEqual(string firstPath, string secondPath)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(firstPath),
            Path.TrimEndingDirectorySeparator(secondPath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>责任人保持草稿顺序，使用中文顿号连接，不推断或补充人员。</summary>
    private static string JoinOwners(IReadOnlyList<string> owners) => string.Join("、", owners);

    /// <summary>用单个 LF 连接同一单元格中的多条内容，null 仅表示空行。</summary>
    private static string JoinLines(IEnumerable<string?> values)
    {
        return string.Join("\n", values.Select(value => value ?? string.Empty));
    }

    /// <summary>NaN 和正负无穷不是合法 Excel 数值，遇到时明确终止导出。</summary>
    private static void EnsureFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Excel 数值必须是有限值。");
        }
    }
}
