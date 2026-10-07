using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalPrinterWeb.Controllers;
using ThermalPrinterWeb.Models;
using ThermalPrinterWeb.Services;
using ThermalPrinterWeb.Services.Printing;
using ThermalPrinterWeb.Services.Printing.Handlers;
using BarcodeType = ThermalPrinterWeb.Models.BarcodeType;

namespace ThermalPrinterWeb.Tests;

// No test here reaches the network: the document is built before the first printer call.
public sealed class PayloadErrorTests
{
    private const string Secret = TestBlocks.Secret;

    private static PrinterService NewService(ILogger<PrinterService>? logger = null) => TestBlocks.NewService(logger);

    private static PrintContent Text(string content = "ok") => new() { Type = ContentType.Text, Content = content };

    private static PrintContent BadBarcode() => new()
    {
        Type = ContentType.Barcode,
        Content = Secret,
        BarcodeOptions = new BarcodeOptions { Type = BarcodeType.EAN13 }
    };

    private static PrintContent Barcode(int? heightInDots, BarLabelPosition labelPosition = BarLabelPosition.Below, string content = "BOX-0007") => new()
    {
        Type = ContentType.Barcode,
        Content = content,
        BarcodeOptions = new BarcodeOptions { HeightInDots = heightInDots, LabelPosition = labelPosition }
    };

    private static PrintContent LineFeed(int lines) => new() { Type = ContentType.LineFeed, Lines = lines };

    internal static string OverPaper(int block, ContentType type)
        => $"Block {block} ({type}): the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)";

    // The feed lines before the auto-cut pass the limit: no block is at fault.
    internal static readonly string OverPaperAtAutoCut =
        $"options.feedLinesAfterPrint: the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)";

    // The same for a job with no feed field: the default lines pass the limit, so the text names no field.
    internal static readonly string OverPaperAtDefaultFeed =
        $"{PrinterService.DefaultFeedCause}: the document is over the limit of {PaperLength.MaxDots} dots of paper ({PaperLength.MaxDots / PaperLength.DotsPerMetre} m)";

    internal static string QRCodeControl(int codePoint, int index)
        => $"content holds the control character U+{codePoint:X4} at index {index}; a QR code takes no control character but a line break (\\n or \\r\\n)";

    public static TheoryData<PrintContent, string> InvalidBlocks() => new()
    {
        { new PrintContent { Type = ContentType.Separator, SeparatorChar = "" }, "Block 1 (Separator): separatorChar must not be empty" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = -1 }, "Block 1 (Separator): separatorLength must not be negative" },
        { new PrintContent { Type = ContentType.Image, Content = "not base64 !!" }, "Block 1 (Image): Image rejected: not valid base64 (13 characters)." },
        { new PrintContent { Type = ContentType.Image, Content = Convert.ToBase64String("not an image"u8) }, "Block 1 (Image): Image rejected: format not supported (format unknown, 12 bytes). Send a PNG or JPEG." },
        { BadBarcode(), "Block 1 (Barcode): content is not a valid EAN13 barcode" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = 65 }, "Block 1 (Separator): separatorLength 65 is over the limit of 64" },
        { new PrintContent { Type = ContentType.Separator, SeparatorLength = int.MaxValue }, "Block 1 (Separator): separatorLength 2147483647 is over the limit of 64" },
        { new PrintContent { Type = ContentType.LineFeed, Lines = 101 }, "Block 1 (LineFeed): lines 101 is over the limit of 100" },
        { Text(new string('x', 10_001)), "Block 1 (Text): text length 10001 is over the limit of 10000" },
        { Text(new string('\n', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        // The encoder turns each of these into LF.
        { Text(new string('\r', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { Text(new string('\f', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { Text(new string('\u2028', 500)), "Block 1 (Text): text line count 501 is over the limit of 500" },
        { new PrintContent { Type = ContentType.Image, Content = "AAAA", ImageOptions = new ImageOptions { MaxWidth = 0 } }, "Block 1 (Image): imageOptions.maxWidth 0 must be at least 1" },
        { new PrintContent { Type = ContentType.Barcode, Content = "Zażółć" }, "Block 1 (Barcode): a CODE128 barcode holds printable ASCII only" },
        { Barcode(0), "Block 1 (Barcode): barcodeOptions.heightInDots 0 is outside the range 1 to 255" },
        { Barcode(-1), "Block 1 (Barcode): barcodeOptions.heightInDots -1 is outside the range 1 to 255" },
        { Barcode(256), "Block 1 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255" },
        // A barcode block with no content prints nothing; the height is still checked.
        { Barcode(256, content: ""), "Block 1 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255" },
        { Barcode(int.MinValue), "Block 1 (Barcode): barcodeOptions.heightInDots -2147483648 is outside the range 1 to 255" },
        { new PrintContent { Type = ContentType.QRCode, Content = new string('ż', 1477) }, "Block 1 (QRCode): content is 2954 bytes as UTF-8; a Model2 QR code holds at most 2953" },
        { new PrintContent { Type = ContentType.QRCode, Content = Secret + "\u001b@" }, $"Block 1 (QRCode): {QRCodeControl(0x1B, Secret.Length)}" },
        { new PrintContent { Type = ContentType.QRCode, Content = "a\tb" }, $"Block 1 (QRCode): {QRCodeControl('\t', 1)}" },
        // The same rule for both code types.
        { new PrintContent { Type = ContentType.Barcode, Content = "AB\u001b@" }, "Block 1 (Barcode): a CODE128 barcode holds printable ASCII only" },
        // JSON "type": 99 binds to a value with no name.
        { new PrintContent { Type = (ContentType)(-1), Content = Secret }, "Block 1 (-1): type is not supported" },
        // The same for every other enum: a number with no name binds.
        { new PrintContent { Type = ContentType.Text, Content = Secret, Alignment = (Alignment)99 }, NotAValidValue(1, "Text", "alignment", "99", AlignmentNames) },
        { new PrintContent { Type = ContentType.Separator, Alignment = (Alignment)(-1) }, NotAValidValue(1, "Separator", "alignment", "-1", AlignmentNames) },
        { new PrintContent { Type = ContentType.Text, Content = Secret, Style = [PrintStyle.Bold, PrintStyle.FontB, (PrintStyle)9] }, NotAValidValue(1, "Text", "style[2]", "9", PrintStyleNames) },
        { new PrintContent { Type = ContentType.Separator, Style = [(PrintStyle)int.MinValue] }, NotAValidValue(1, "Separator", "style[0]", "-2147483648", PrintStyleNames) },
        { new PrintContent { Type = ContentType.Barcode, Content = Secret, BarcodeOptions = new BarcodeOptions { Type = (BarcodeType)10 } }, NotAValidValue(1, "Barcode", "barcodeOptions.type", "10", BarcodeTypeNames) },
        { new PrintContent { Type = ContentType.Barcode, Content = Secret, BarcodeOptions = new BarcodeOptions { Width = (BarWidth)3 } }, NotAValidValue(1, "Barcode", "barcodeOptions.width", "3", BarWidthNames) },
        { new PrintContent { Type = ContentType.Barcode, Content = Secret, BarcodeOptions = new BarcodeOptions { LabelPosition = (BarLabelPosition)4 } }, NotAValidValue(1, "Barcode", "barcodeOptions.labelPosition", "4", BarLabelPositionNames) },
        { new PrintContent { Type = ContentType.QRCode, Content = Secret, QRCodeOptions = new QRCodeOptions { Model = (QRCodeModel)3 } }, NotAValidValue(1, "QRCode", "qrCodeOptions.model", "3", QRCodeModelNames) },
        { new PrintContent { Type = ContentType.QRCode, Content = Secret, QRCodeOptions = new QRCodeOptions { Size = (QRCodeSize)3 } }, NotAValidValue(1, "QRCode", "qrCodeOptions.size", "3", QRCodeSizeNames) },
        { new PrintContent { Type = ContentType.QRCode, Content = Secret, QRCodeOptions = new QRCodeOptions { CorrectionLevel = (QRCodeCorrectionLevel)4 } }, NotAValidValue(1, "QRCode", "qrCodeOptions.correctionLevel", "4", QRCodeCorrectionLevelNames) },
        { TestBlocks.Signal(count: 0), "Block 1 (Signal): signalOptions.count 0 is outside the range 1 to 9" },
        { TestBlocks.Signal(count: 10), "Block 1 (Signal): signalOptions.count 10 is outside the range 1 to 9" },
        { TestBlocks.Signal(duration: 0), "Block 1 (Signal): signalOptions.duration 0 is outside the range 1 to 9" },
        { TestBlocks.Signal(duration: int.MaxValue), "Block 1 (Signal): signalOptions.duration 2147483647 is outside the range 1 to 9" },
        // 0 is the default of the enum and has no name.
        { TestBlocks.Signal(mode: 0), NotAValidValue(1, "Signal", "signalOptions.mode", "0", SignalModeNames) },
        { TestBlocks.Signal(mode: (SignalMode)4), NotAValidValue(1, "Signal", "signalOptions.mode", "4", SignalModeNames) },
        // A block with no content prints nothing; its enum values are still checked.
        { new PrintContent { Type = ContentType.QRCode, QRCodeOptions = new QRCodeOptions { Size = (QRCodeSize)(-1) } }, NotAValidValue(1, "QRCode", "qrCodeOptions.size", "-1", QRCodeSizeNames) },
        // Also in an options object that the block type does not read.
        { new PrintContent { Type = ContentType.Text, Content = Secret, BarcodeOptions = new BarcodeOptions { Width = (BarWidth)99 } }, NotAValidValue(1, "Text", "barcodeOptions.width", "99", BarWidthNames) },
        // The first field at fault is named.
        { new PrintContent { Type = ContentType.Text, Alignment = (Alignment)3, Style = [(PrintStyle)99] }, NotAValidValue(1, "Text", "alignment", "3", AlignmentNames) }
    };

    // Written out, not read from the enums: a renamed or reordered member must fail a test.
    internal const string AlignmentNames = "Left, Center or Right";
    // Without the members of no effect on this printer (BlockEnums.HasNoEffect): Italic, GS1_128, GS1_DATABAR_OMNIDIRECTIONAL.
    internal const string PrintStyleNames = "Normal, Bold, Underline, DoubleHeight, DoubleWidth, FontB, ReverseMode or UpsideDownMode";
    internal const string BarcodeTypeNames = "UPC_A, UPC_E, EAN13, EAN8, CODE39, CODE128, ITF or CODABAR";
    internal const string BarWidthNames = "Thin, Default or Thick";
    internal const string BarLabelPositionNames = "None, Above, Below or Both";
    internal const string QRCodeModelNames = "Model1, Model2 or Micro";
    internal const string QRCodeSizeNames = "Normal, Large or ExtraLarge";
    internal const string QRCodeCorrectionLevelNames = "Percent7, Percent15, Percent25 or Percent30";
    internal const string SignalModeNames = "Sound, Light or SoundAndLight";

    internal static string NotAValidValue(int block, string blockType, string field, string number, string validNames)
        => $"Block {block} ({blockType}): {field} {number} is not a valid value; use {validNames}";

    // JSON "alignment": 99 printed centered and answered 200.
    [Fact]
    public async Task PrintAsync_EnumNumberWithNoName_IsLoggedOnceAsAWarning()
    {
        var logger = new RecordingLogger<PrinterService>();
        List<PrintContent> content = [Text(), new PrintContent { Type = ContentType.Text, Content = Secret, Alignment = (Alignment)99 }];

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(PrintResult.Invalid("Block 1 (Text): alignment 99 is not a valid value; use Left, Center or Right"), result);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(
            (LogLevel.Warning, "Rejected print: block 1 (Text) failed with PrintContentException: alignment 99 is not a valid value; use Left, Center or Right"),
            entry);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(Secret, e.Message));
    }

    public static TheoryData<PrintContent> BlocksWithEveryEnumMember()
    {
        var data = new TheoryData<PrintContent>();
        foreach (var alignment in Enum.GetValues<Alignment>())
            data.Add(new PrintContent { Type = ContentType.Text, Content = "x", Alignment = alignment });
        data.Add(new PrintContent { Type = ContentType.Text, Content = "x", Style = [.. Enum.GetValues<PrintStyle>()] });
        // 6 characters: within the paper at every bar width (BarcodeWidthTests).
        foreach (var width in Enum.GetValues<BarWidth>())
            data.Add(new PrintContent { Type = ContentType.Barcode, Content = "BOX-07", BarcodeOptions = new BarcodeOptions { Width = width } });
        foreach (var position in Enum.GetValues<BarLabelPosition>())
            data.Add(new PrintContent { Type = ContentType.Barcode, Content = "BOX-0007", BarcodeOptions = new BarcodeOptions { LabelPosition = position } });
        foreach (var model in Enum.GetValues<QRCodeModel>())
            data.Add(new PrintContent { Type = ContentType.QRCode, Content = "x", QRCodeOptions = new QRCodeOptions { Model = model } });
        foreach (var size in Enum.GetValues<QRCodeSize>())
            data.Add(new PrintContent { Type = ContentType.QRCode, Content = "x", QRCodeOptions = new QRCodeOptions { Size = size } });
        foreach (var level in Enum.GetValues<QRCodeCorrectionLevel>())
            data.Add(new PrintContent { Type = ContentType.QRCode, Content = "x", QRCodeOptions = new QRCodeOptions { CorrectionLevel = level } });
        foreach (var mode in Enum.GetValues<SignalMode>())
            data.Add(TestBlocks.Signal(mode));
        // No content: the symbology rules for the data are not the subject here.
        foreach (var type in Enum.GetValues<BarcodeType>())
            data.Add(new PrintContent { Type = ContentType.Barcode, BarcodeOptions = new BarcodeOptions { Type = type } });
        // Fields the JSON left out or set to null.
        data.Add(new PrintContent { Type = ContentType.Barcode, Content = "BOX-0007", BarcodeOptions = new BarcodeOptions { Width = null, LabelPosition = null } });
        data.Add(new PrintContent { Type = ContentType.Text, Content = "x", Style = [] });
        data.Add(new PrintContent { Type = ContentType.Signal });
        data.Add(TestBlocks.Signal());
        return data;
    }

    [Theory]
    [MemberData(nameof(BlocksWithEveryEnumMember))]
    public async Task BuildDocumentAsync_DefinedEnumValue_IsAccepted(PrintContent block)
    {
        Assert.NotEmpty(await NewService().BuildDocumentAsync([block], null));
    }

    // The list in BlockEnums is written by hand: an enum property added to a model with no check there fails here.
    [Fact]
    public async Task BuildDocumentAsync_EveryEnumPropertyOfABlock_RejectsANumberWithNoName()
    {
        var checkedProperties = 0;
        foreach (var owner in new[] { typeof(PrintContent), typeof(BarcodeOptions), typeof(QRCodeOptions), typeof(ImageOptions), typeof(SignalOptions) })
        {
            foreach (var property in owner.GetProperties())
            {
                // The block type has its own check.
                if (property == typeof(PrintContent).GetProperty(nameof(PrintContent.Type)) || UndefinedEnumValue(property.PropertyType) is not { } value)
                    continue;

                var block = new PrintContent { Type = ContentType.Text, Content = "x", BarcodeOptions = new BarcodeOptions(), QRCodeOptions = new QRCodeOptions(), ImageOptions = new ImageOptions(), SignalOptions = new SignalOptions() };
                var target = owner == typeof(PrintContent)
                    ? block
                    : typeof(PrintContent).GetProperties().Single(options => options.PropertyType == owner).GetValue(block);
                property.SetValue(target, value);

                var exception = await Record.ExceptionAsync(() => NewService().BuildDocumentAsync([block], null));

                Assert.True(
                    exception is PrintContentException && exception.Message.Contains("99 is not a valid value", StringComparison.Ordinal),
                    $"{owner.Name}.{property.Name} takes a number with no name");
                checkedProperties++;
            }
        }

        Assert.Equal(9, checkedProperties);
        // The job options are not part of a block: an enum there needs its own check.
        Assert.DoesNotContain(typeof(PrintOptions).GetProperties(), property => UndefinedEnumValue(property.PropertyType) is not null);
    }

    // Null: the property holds no enum. Else 99 as the enum, or a list with that one entry.
    private static object? UndefinedEnumValue(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type.IsEnum)
            return Enum.ToObject(type, 99);

        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>) || !type.GetGenericArguments()[0].IsEnum)
            return null;

        var list = (IList)Activator.CreateInstance(type)!;
        list.Add(Enum.ToObject(type.GetGenericArguments()[0], 99));
        return list;
    }

    [Fact]
    public async Task PrintAsync_UnknownBlockType_IsLoggedOnceAsAWarning()
    {
        var logger = new RecordingLogger<PrinterService>();

        var result = await NewService(logger).PrintAsync([Text(), new PrintContent { Type = (ContentType)99, Content = Secret }]);

        Assert.Equal(PrintResult.Invalid("Block 1 (99): type is not supported"), result);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal((LogLevel.Warning, "Rejected print: block 1 has the unsupported type 99"), entry);
    }

    [Fact]
    public async Task BuildDocumentAsync_BlocksAtTheLimits_AreAccepted()
    {
        List<PrintContent> content =
        [
            new() { Type = ContentType.Separator, SeparatorLength = 64 },
            new() { Type = ContentType.LineFeed, Lines = 100 },
            Text(new string('x', 10_000)),
            Text(new string('\n', 499)),
            // No paper: the document stays under the paper limit.
            .. Enumerable.Range(0, PrinterService.MaxBlocks - 4).Select(_ => new PrintContent { Type = ContentType.CodePage, Content = "PC852" })
        ];
        Assert.Equal(PrinterService.MaxBlocks, content.Count);

        Assert.NotEmpty(await NewService().BuildDocumentAsync(content, null));
    }

    [Fact]
    public async Task PrintAsync_TooManyBlocks_IsAValidationFailureLoggedOnce()
    {
        var logger = new RecordingLogger<PrinterService>();
        var content = Enumerable.Range(0, PrinterService.MaxBlocks + 1).Select(_ => Text(Secret)).ToList();

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(PrintResult.Invalid("block count 501 is over the limit of 500"), result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(Secret, entry.Message);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public async Task PrintAsync_ImageBlocks_AreLimitedPerDocument(int count, bool accepted)
    {
        var logger = new RecordingLogger<PrinterService>();
        // Not an image: an accepted document fails later, at block 0.
        var content = Enumerable.Range(0, count).Select(_ => new PrintContent { Type = ContentType.Image, Content = "AAAA" }).ToList();
        // Image blocks with no picture do not count.
        content.Add(new PrintContent { Type = ContentType.Image });

        var result = await NewService(logger).PrintAsync(content);

        Assert.Equal(PrintFailure.Validation, result.Failure);
        Assert.Equal(accepted, result.Error!.StartsWith("Block 0 (Image):", StringComparison.Ordinal));
        if (!accepted)
            Assert.Equal("image block count 21 is over the limit of 20", result.Error);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information).Level);
    }

    [Fact]
    public async Task PrintAsync_TooMuchPrinterData_IsAValidationFailureLoggedOnce()
    {
        var logger = new RecordingLogger<PrinterService>();
        var content = Enumerable.Range(0, 250).Select(_ => Text()).ToList();

        // Text and images pass the paper limit first, so the handler here adds bytes and no paper.
        var result = await new PrinterService(logger, [new BulkHandler()], NoPrinter.Options).PrintAsync(content);

        // 2 MiB / 10,003 bytes per block = 209 blocks fit.
        Assert.Equal(PrintResult.Invalid("Block 209: the document is over the limit of 2097152 bytes of printer data"), result);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    private sealed class BulkHandler : IBlockHandler
    {
        public ContentType Type => ContentType.Text;

        public Task HandleAsync(PrintContent item, BlockContext ctx)
        {
            ctx.Add(new byte[10_000]);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public async Task BuildDocumentAsync_BarcodeHeightAtTheLimits_GoesToThePrinter(int height)
    {
        var bytes = await NewService().BuildDocumentAsync([Barcode(height)], null);

        // GS h n
        Assert.Contains(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1D, (byte)0x68, (byte)height]));
    }

    [Fact]
    public async Task BuildDocumentAsync_BarcodeWithoutHeight_KeepsThePrinterDefault()
    {
        var bytes = await NewService().BuildDocumentAsync([Barcode(null)], null);

        Assert.DoesNotContain(bytes, command => command.AsSpan().StartsWith([(byte)0x1D, (byte)0x68]));
    }

    [Theory]
    [InlineData(-1, null, "options.defaultLineSpacing -1 is outside the range 0 to 255")]
    [InlineData(256, null, "options.defaultLineSpacing 256 is outside the range 0 to 255")]
    [InlineData(-70000, null, "options.defaultLineSpacing -70000 is outside the range 0 to 255")]
    [InlineData(null, -1, "options.feedLinesAfterPrint -1 is outside the range 0 to 255")]
    [InlineData(null, 256, "options.feedLinesAfterPrint 256 is outside the range 0 to 255")]
    [InlineData(null, int.MinValue, "options.feedLinesAfterPrint -2147483648 is outside the range 0 to 255")]
    public async Task PrintAsync_OptionOutsideThePrinterRange_IsAValidationFailureLoggedOnce(int? lineSpacing, int? feed, string expectedError)
    {
        var logger = new RecordingLogger<PrinterService>();
        var options = new PrintOptions { DefaultLineSpacing = lineSpacing, FeedLinesAfterPrint = feed };

        var result = await NewService(logger).PrintAsync([Text()], options);

        Assert.Equal(PrintResult.Invalid(expectedError), result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Rejected print: " + expectedError, entry.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 255)]
    [InlineData(255, 0)]
    public async Task BuildDocumentAsync_OptionsAtTheLimits_GoToThePrinter(int lineSpacing, int feed)
    {
        var options = new PrintOptions { DefaultLineSpacing = lineSpacing, FeedLinesAfterPrint = feed };

        var bytes = await NewService().BuildDocumentAsync([Text()], options);

        // ESC 3 n, then the feed lines and GS V 65 3 at the end. No ESC 2 after it: the next job starts with ESC @.
        Assert.Contains(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1B, (byte)0x33, (byte)lineSpacing]));
        Assert.Equal([0x1D, 0x56, 0x41, 3], bytes[^1]);
        if (feed > 0)
            Assert.Equal(Enumerable.Repeat((byte)0x0A, feed), bytes[^2]);
        Assert.DoesNotContain(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1B, (byte)0x32]));
    }

    [Fact]
    public async Task BuildDocumentAsync_NoLineSpacing_SendsNoSpacingCommand()
    {
        var bytes = await NewService().BuildDocumentAsync([Text()], new PrintOptions());

        Assert.DoesNotContain(bytes, command => command.AsSpan().StartsWith([(byte)0x1B, (byte)0x33]));
        Assert.DoesNotContain(bytes, command => command.AsSpan().SequenceEqual([(byte)0x1B, (byte)0x32]));
    }

    public static TheoryData<List<PrintContent>, PrintOptions?, string?> PaperJobs()
    {
        var doubleHeight = new PrintContent { Type = ContentType.Text, Content = new string('\n', 499), Style = [PrintStyle.DoubleHeight] };
        var widestQRCode = new PrintContent
        {
            Type = ContentType.QRCode,
            Content = new string('x', 2953),
            QRCodeOptions = new QRCodeOptions { Size = QRCodeSize.ExtraLarge }
        };
        var cut = new PrintContent { Type = ContentType.Cut };

        return new()
        {
            // One line is 29 dots: 11 x 2900 + 3 x 29 = 31,987.
            { [.. Enumerable.Repeat(LineFeed(100), 11), LineFeed(3)], null, null },
            { [.. Enumerable.Repeat(LineFeed(100), 11), LineFeed(4)], null, OverPaper(11, ContentType.LineFeed) },
            // The job from the issue: 500 blocks of 100 lines.
            { [.. Enumerable.Repeat(LineFeed(100), 500)], null, OverPaper(11, ContentType.LineFeed) },
            // 500 DoubleHeight lines are 26,500 dots.
            { [doubleHeight], null, null },
            { [doubleHeight, doubleHeight], null, OverPaper(1, ContentType.Text) },
            // A long line wraps: 10,000 characters are 417 DoubleWidth lines.
            { [.. Enumerable.Repeat(new PrintContent { Type = ContentType.Text, Content = new string('x', 10_000), Style = [PrintStyle.DoubleWidth] }, 3)], null, OverPaper(2, ContentType.Text) },
            // Line spacing 255: 125 lines are 31,875 dots. A feed of 0: the limit of the block alone.
            { [Text(new string('\n', 124))], new PrintOptions { DefaultLineSpacing = 255, FeedLinesAfterPrint = 0 }, null },
            { [Text(new string('\n', 125))], new PrintOptions { DefaultLineSpacing = 255 }, OverPaper(0, ContentType.Text) },
            // No feed field: the 3 default lines before the auto-cut count, here 3 x 255 dots. No block and no field is at fault.
            { [Text(new string('\n', 124))], new PrintOptions { DefaultLineSpacing = 255 }, OverPaperAtDefaultFeed },
            // Each cut feeds 255 lines of 29 dots, the 3 units of the cut command and the 124 dots to the cutter: 7522.
            { [.. Enumerable.Repeat(cut, 4)], new PrintOptions { FeedLinesAfterPrint = 255 }, null },
            { [.. Enumerable.Repeat(cut, 5)], new PrintOptions { FeedLinesAfterPrint = 255 }, OverPaper(4, ContentType.Cut) },
            // A feed of 0 still moves the paper to the cutter: 127 dots.
            { [.. Enumerable.Repeat(cut, 251)], new PrintOptions { FeedLinesAfterPrint = 0 }, null },
            { [.. Enumerable.Repeat(cut, 252)], new PrintOptions { FeedLinesAfterPrint = 0 }, OverPaper(251, ContentType.Cut) },
            // No feed field: each cut with no LineFeed block before it also feeds the 3 default lines. 3 x 29 + 127 = 214 dots.
            { [.. Enumerable.Repeat(cut, 149)], null, null },
            { [.. Enumerable.Repeat(cut, 150)], null, OverPaper(149, ContentType.Cut) },
            // A LineFeed block of 3 lines before each cut: the same 214 dots, the cut adds no line.
            { [.. Enumerable.Repeat<PrintContent[]>([LineFeed(3), cut], 149).SelectMany(pair => pair)], null, null },
            // The auto-cut counts its feed lines, not its cut command: 255 lines of 29 dots are 7395 dots.
            { [], new PrintOptions { FeedLinesAfterPrint = 255 }, null },
            // A line of the feed has the line spacing of the job: 125 lines of 255 dots are 31,875 dots.
            { [], new PrintOptions { DefaultLineSpacing = 255, FeedLinesAfterPrint = 125 }, null },
            { [], new PrintOptions { DefaultLineSpacing = 255, FeedLinesAfterPrint = 126 }, OverPaperAtAutoCut },
            { [Text()], new PrintOptions { DefaultLineSpacing = 255, FeedLinesAfterPrint = 125 }, OverPaperAtAutoCut },
            // No feed field and 3 empty lines at the end: the auto-cut adds no paper, so a document at the limit still passes.
            { [.. Enumerable.Repeat(LineFeed(100), 11), LineFeed(3)], new PrintOptions(), null },
            // The printer wraps on bytes. KATAKANA has no .NET encoding, so the text goes out as UTF-8: 3 bytes for one euro sign.
            { [.. Enumerable.Repeat(Text(new string('€', 10_000)), 2)], new PrintOptions { CodePage = "KATAKANA" }, OverPaper(1, ContentType.Text) },
            // PC852 has no ellipsis: it prints as three dots.
            { [.. Enumerable.Repeat(Text(new string('…', 10_000)), 2)], null, OverPaper(1, ContentType.Text) },
            // 177 modules x 6 dots + one line = 1091 dots.
            { [.. Enumerable.Repeat(widestQRCode, 29)], null, null },
            { [.. Enumerable.Repeat(widestQRCode, 30)], null, OverPaper(29, ContentType.QRCode) },
            // 255 dots + one line = 284 dots.
            { [.. Enumerable.Repeat(Barcode(255), 112)], null, null },
            { [.. Enumerable.Repeat(Barcode(255), 113)], null, OverPaper(112, ContentType.Barcode) },
            // A caption above and below: 255 dots + two lines = 313 dots. 102 are 31,926 dots: with a feed of 0 they pass,
            // with no feed field the 3 default lines (87 dots) are over the limit.
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 102)], new PrintOptions { FeedLinesAfterPrint = 0 }, null },
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 102)], null, OverPaperAtDefaultFeed },
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 101)], null, null },
            { [.. Enumerable.Repeat(Barcode(255, BarLabelPosition.Both), 103)], null, OverPaper(102, ContentType.Barcode) }
        };
    }

    [Theory]
    [MemberData(nameof(PaperJobs))]
    public async Task BuildDocumentAsync_PaperLength_IsLimitedPerDocument(List<PrintContent> content, PrintOptions? options, string? expectedError)
    {
        var logger = new RecordingLogger<PrinterService>();

        // Not PrintAsync: an accepted document would go to the printer.
        var build = NewService(logger).BuildDocumentAsync(content, options);

        if (expectedError is null)
        {
            Assert.NotEmpty(await build);
            return;
        }

        Assert.Equal(expectedError, (await Assert.ThrowsAsync<PrintContentException>(() => build)).Message);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Theory]
    [MemberData(nameof(InvalidBlocks))]
    public async Task PrintAsync_InvalidBlock_IsAValidationFailure(PrintContent block, string expectedError)
    {
        var result = await NewService().PrintAsync([Text(), block]);

        Assert.False(result.Success);
        Assert.Equal(PrintFailure.Validation, result.Failure);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public async Task PrintAsync_InvalidBlock_KeepsCallerContentOutOfErrorAndLog()
    {
        var logger = new RecordingLogger<PrinterService>();

        var result = await NewService(logger).PrintAsync([BadBarcode()]);

        Assert.DoesNotContain(Secret, result.Error);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(Secret, e.Message));
        // A caller's bad payload is logged once, and not as an error.
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("block 0 (Barcode)", entry.Message);
    }

    [Fact]
    public async Task PrintAsync_QRCodeWithControlCharacter_IsLoggedOnceWithNoContentAndNoReplacementCount()
    {
        var logger = new RecordingLogger<PrinterService>();

        var result = await NewService(logger).PrintAsync([new PrintContent { Type = ContentType.QRCode, Content = Secret + "\u001b@\u0000" }]);

        Assert.Equal(PrintFailure.Validation, result.Failure);
        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Information);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"Rejected print: block 0 (QRCode) failed with PrintContentException: {QRCodeControl(0x1B, Secret.Length)}", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain(Secret, e.Message));
        Assert.All(logger.Entries, e => Assert.DoesNotContain("Replaced", e.Message));
    }

    [Fact]
    public async Task BuildDocumentAsync_QRCodeNextToReplacedText_CountsOnlyTheText()
    {
        var logger = new RecordingLogger<PrinterService>();

        await NewService(logger).BuildDocumentAsync(
            [Text("a\u001bb"), new PrintContent { Type = ContentType.QRCode, Content = "line 1\r\nline 2\nŻółw" }],
            null);

        Assert.Contains(logger.Entries, e => e is { Level: LogLevel.Information, Message: "Replaced 1 unprintable character(s) with '?'" });
    }

    [Fact]
    public async Task PrintAsync_NullBlock_IsAValidationFailure()
    {
        var result = await NewService().PrintAsync([Text(), null!]);

        Assert.Equal(PrintResult.Invalid("Block 1: must not be null"), result);
    }

    // The name is caller text: it reaches the log cleaned and cut.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildDocumentAsync_UnknownCodePage_LogsACleanName(bool asBlock)
    {
        var hostile = "nope\r\nFAKE LOG LINE\u001b[31m" + new string('x', 10_240);
        var logger = new RecordingLogger<PrinterService>();
        var blockLogger = new RecordingLogger<CodePageBlockHandler>();
        var service = new PrinterService(logger, [new TextBlockHandler(), new CodePageBlockHandler(blockLogger)], NoPrinter.Options);

        if (asBlock)
            await service.BuildDocumentAsync([new PrintContent { Type = ContentType.CodePage, Content = hostile }, Text()], null);
        else
            await service.BuildDocumentAsync([Text()], new PrintOptions { CodePage = hostile });

        var entry = Assert.Single(asBlock ? blockLogger.Entries : logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith("Unknown code page \"nope??FAKE LOG LINE?[31mxxxxxxxx\"", entry.Message);
        Assert.DoesNotContain('\n', entry.Message);
        Assert.True(entry.Message.Length < 100, entry.Message);
    }

    private static async Task<ObjectResult> PrintViaControllerAsync(PrintResult result)
    {
        var controller = new PrinterController(new RecordingPrinter { Result = result }, new PrintJobLog(NullLogger<PrintJobLog>.Instance, new HttpContextAccessor()));
        return Assert.IsAssignableFrom<ObjectResult>(await controller.Print(new PrintRequest { Content = [Text()] }, TimeProvider.System));
    }

    [Fact]
    public async Task Print_ValidationFailure_Returns400()
    {
        var response = await PrintViaControllerAsync(PrintResult.Invalid("bad block"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "bad block", "validation"), response.Value);
    }

    [Fact]
    public async Task Print_PrinterFault_Returns503()
    {
        var response = await PrintViaControllerAsync(PrintResult.PrinterFault("Printer not ready: cover open"));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "Printer not ready: cover open", "printer"), response.Value);
    }

    [Fact]
    public async Task Print_Success_Returns200()
    {
        var response = await PrintViaControllerAsync(PrintResult.Ok);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new PrintResponse(true), response.Value);
    }
}

// Real PrinterService behind the HTTP pipeline: every request fails before the first printer call.
public sealed class PayloadErrorHttpTests(ClosedPortApp app) : IClassFixture<ClosedPortApp>
{
    private readonly HttpClient _client = app.CreateClient();

    private async Task<PrintResponse> PostBadRequestAsync(string json)
    {
        var response = await _client.PostAsync("/api/printer", TestHttp.Json(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PrintResponse>();
        Assert.NotNull(body);
        Assert.False(body.Success);
        Assert.Equal("validation", body.Type);
        return body;
    }

    [Fact]
    public async Task PostPrinter_EmptySeparatorChar_Returns400WithReason()
    {
        var body = await PostBadRequestAsync("""{"content":[{"type":"Separator","separatorChar":""}]}""");

        Assert.Equal("Block 0 (Separator): separatorChar must not be empty", body.Error);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"Bogus"}]}""", "$.content[0].type")]
    [InlineData("""{"content":[{"type":true}]}""", "$.content[0].type")]
    [InlineData("""{"content":[{"type":1.5}]}""", "$.content[0].type")]
    [InlineData("""{"content":[{"type":99999999999}]}""", "$.content[0].type")]
    [InlineData("""{"content":[{"type":"Text","alignment":"Middle"}]}""", "$.content[0].alignment")]
    [InlineData("""{"content":[{"type":"Text","style":["Bold","Huge"]}]}""", "$.content[0].style[1]")]
    [InlineData("""{"content":[{"type":"LineFeed","lines":"three"}]}""", "$.content[0].lines")]
    [InlineData("""{"content":[{"type":"Text","content":5}]}""", "$.content[0].content")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"autoCut":"yes"}}""", "$.options.autoCut")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"feedLinesAfterPrint":"three"}}""", "$.options.feedLinesAfterPrint")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"feedLinesAfterPrint":1.5}}""", "$.options.feedLinesAfterPrint")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"feedLinesAfterPrint":99999999999}}""", "$.options.feedLinesAfterPrint")]
    [InlineData("""{"content":"text"}""", "$.content")]
    [InlineData("""{"content":[""", "$.content[0]")]
    [InlineData("""{"name":5,"message":"m"}""", "$.name")]
    [InlineData("[]", "$")]
    [MemberData(nameof(UnknownEnumNames))]
    public async Task PostPrinter_ModelBindingError_ReturnsThePathWithOwnText(string json, string expectedPath)
    {
        var body = await PostBadRequestAsync(json);

        // The whole text: no System.Text.Json message, so no CLR type name (ThermalPrinterWeb.Models.ContentType, System.Nullable`1[System.Int32]).
        Assert.Equal($"{expectedPath}: malformed JSON, wrong JSON type or unknown name", body.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task PostPrinter_NoBody_Returns400WithoutClrTypeName(string json)
    {
        var body = await PostBadRequestAsync(json);

        Assert.NotNull(body.Error);
        Assert.DoesNotContain("ThermalPrinterWeb", body.Error);
        Assert.DoesNotContain("System.", body.Error);
    }

    // The closed loopback port answers 503 for a job that passes validation: a 400 shows that nothing went to the printer.
    [Theory]
    [InlineData("""{"content":[{"type":99}]}""", "Block 0 (99): type is not supported")]
    [InlineData("""{"content":[{"type":"Text","content":"x"},{"type":9,"content":"SECRET"}]}""", "Block 1 (9): type is not supported")]
    [InlineData("""{"content":[{"type":-1}]}""", "Block 0 (-1): type is not supported")]
    // The enum converter reads a number in a string as the number.
    [InlineData("""{"content":[{"type":"99"}]}""", "Block 0 (99): type is not supported")]
    [MemberData(nameof(EnumNumbersWithNoName))]
    public async Task PostPrinter_EnumNumberWithNoName_Returns400(string json, string expectedError)
    {
        var body = await PostBadRequestAsync(json);

        Assert.Equal(expectedError, body.Error);
    }

    // A number that names an enum member and a name in any casing stay valid: the job reaches the printer step.
    [Theory]
    [InlineData("""{"content":[{"type":0,"content":"x"}]}""")]
    [InlineData("""{"content":[{"type":"text","content":"x"}]}""")]
    [InlineData("""{"content":[{"type":4},{"type":6},{"type":5}]}""")]
    [MemberData(nameof(ValidEnumForms))]
    public async Task PostPrinter_DefinedEnumNumberOrNameInAnyCase_PassesValidation(string json)
    {
        var response = await _client.PostAsync("/api/printer", TestHttp.Json(json));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "Printer not ready: printer unreachable", "printer"), await response.Content.ReadFromJsonAsync<PrintResponse>());
    }

    [Fact]
    public async Task McpPrint_UnknownNumericBlockType_IsNotPrinted()
    {
        var (_, text) = await _client.CallToolAsync("print", """{"content":[{"type":99}]}""");

        Assert.Equal("Not printed: Block 0 (99): type is not supported", text);
    }

    private const string ValuePlaceholder = "VALUE";

    // One row per enum field of a block: the block JSON, the field, a number that names a member, a member name, the names in the error.
    private static readonly (string Block, string BlockType, string Field, string DefinedNumber, string Name, string ValidNames)[] EnumFields =
    [
        ("""{"type":"Text","content":"x","alignment":VALUE}""", "Text", "alignment", "2", "Right", PayloadErrorTests.AlignmentNames),
        ("""{"type":"Text","content":"x","style":["Bold",VALUE]}""", "Text", "style[1]", "4", "DoubleHeight", PayloadErrorTests.PrintStyleNames),
        ("""{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"type":VALUE}}""", "Barcode", "barcodeOptions.type", "5", "CODE128", PayloadErrorTests.BarcodeTypeNames),
        // 6 characters: within the paper also at Thick (BarcodeWidthTests).
        ("""{"type":"Barcode","content":"BOX-07","barcodeOptions":{"width":VALUE}}""", "Barcode", "barcodeOptions.width", "0", "Thick", PayloadErrorTests.BarWidthNames),
        ("""{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"labelPosition":VALUE}}""", "Barcode", "barcodeOptions.labelPosition", "3", "Above", PayloadErrorTests.BarLabelPositionNames),
        ("""{"type":"QRCode","content":"x","qrCodeOptions":{"model":VALUE}}""", "QRCode", "qrCodeOptions.model", "0", "Micro", PayloadErrorTests.QRCodeModelNames),
        ("""{"type":"QRCode","content":"x","qrCodeOptions":{"size":VALUE}}""", "QRCode", "qrCodeOptions.size", "2", "ExtraLarge", PayloadErrorTests.QRCodeSizeNames),
        ("""{"type":"QRCode","content":"x","qrCodeOptions":{"correctionLevel":VALUE}}""", "QRCode", "qrCodeOptions.correctionLevel", "3", "Percent25", PayloadErrorTests.QRCodeCorrectionLevelNames)
    ];

    private static string JobWith(string block, string value)
        => "{\"content\":[" + block.Replace(ValuePlaceholder, value, StringComparison.Ordinal) + "]}";

    public static TheoryData<string, string> EnumNumbersWithNoName()
    {
        var data = new TheoryData<string, string>();
        foreach (var (block, blockType, field, _, _, validNames) in EnumFields)
        {
            data.Add(JobWith(block, "99"), PayloadErrorTests.NotAValidValue(0, blockType, field, "99", validNames));
            data.Add(JobWith(block, "-1"), PayloadErrorTests.NotAValidValue(0, blockType, field, "-1", validNames));
            // The enum converter reads a number in a string as the number.
            data.Add(JobWith(block, "\"99\""), PayloadErrorTests.NotAValidValue(0, blockType, field, "99", validNames));
        }

        return data;
    }

    public static TheoryData<string> ValidEnumForms()
    {
        var data = new TheoryData<string>();
        foreach (var (block, _, _, definedNumber, name, _) in EnumFields)
        {
            data.Add(JobWith(block, definedNumber));
            data.Add(JobWith(block, $"\"{name}\""));
            data.Add(JobWith(block, $"\"{name.ToLowerInvariant()}\""));
            data.Add(JobWith(block, $"\"{name.ToUpperInvariant()}\""));
        }

        // Every enum field left out, with and without its options object.
        data.Add("""{"content":[{"type":"Text","content":"x"},{"type":"Barcode","content":"BOX-0007"},{"type":"QRCode","content":"x"}]}""");
        data.Add("""{"content":[{"type":"Barcode","content":"BOX-0007","barcodeOptions":{}},{"type":"QRCode","content":"x","qrCodeOptions":{}}]}""");
        // The two fields that take null, and a style list with no entry.
        data.Add("""{"content":[{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"width":null,"labelPosition":null}},{"type":"Text","content":"x","style":[]}]}""");
        return data;
    }

    // A name that does not exist fails in model binding.
    public static TheoryData<string, string> UnknownEnumNames()
    {
        var data = new TheoryData<string, string>();
        foreach (var (block, _, field, _, _, _) in EnumFields)
            data.Add(JobWith(block, "\"Bogus\""), $"$.content[0].{field}");
        return data;
    }

    public static TheoryData<string, string> McpEnumNumbersWithNoName()
    {
        var data = new TheoryData<string, string>();
        foreach (var (block, blockType, field, _, _, validNames) in EnumFields)
            data.Add(JobWith(block, "99"), "Not printed: " + PayloadErrorTests.NotAValidValue(0, blockType, field, "99", validNames));
        return data;
    }

    [Theory]
    [MemberData(nameof(McpEnumNumbersWithNoName))]
    public async Task McpPrint_EnumNumberWithNoName_IsNotPrinted(string arguments, string expectedText)
    {
        var (isError, text) = await _client.CallToolAsync("print", arguments);

        Assert.False(isError);
        Assert.Equal(expectedText, text);
    }

    [Fact]
    public async Task McpPrint_DefinedEnumNumbersAndLowerCaseNames_PassValidation()
    {
        var (_, text) = await _client.CallToolAsync(
            "print",
            """{"content":[{"type":"Text","content":"x","alignment":0,"style":["bold",4]},{"type":"QRCode","content":"x","qrCodeOptions":{"model":"model2","size":1,"correctionLevel":"percent15"}},{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"type":5,"width":"thin","labelPosition":3}}]}""");

        Assert.Equal("Not printed: Printer not ready: printer unreachable", text);
    }

    public static TheoryData<string, string?> WrongContentTypes() => new()
    {
        { "text/plain", null },
        { "text/plain", "utf-8" },
        { "application/x-www-form-urlencoded", null },
        { "application/xml", null }
    };

    // MVC reads a form before it looks for a body formatter: a form with no boundary fails there.
    [Fact]
    public async Task PostPrinter_FormWithNoBoundary_Returns400InPrintResponseShape()
    {
        using var content = new ByteArrayContent("x"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");

        var response = await _client.PostAsync("/api/printer", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            new PrintResponse(false, "Failed to read the request form. Missing content-type boundary.", "validation"),
            await response.Content.ReadFromJsonAsync<PrintResponse>());
    }

    [Theory]
    [MemberData(nameof(WrongContentTypes))]
    public async Task PostPrinter_WrongContentType_Returns415InPrintResponseShape(string mediaType, string? charset)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"content":[{"type":"Text","content":"SECRET"}]}"""));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = charset };

        await AssertUnsupportedMediaTypeAsync(content);
    }

    [Fact]
    public async Task PostPrinter_NoContentType_Returns415InPrintResponseShape()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"content":[{"type":"Text","content":"SECRET"}]}"""));
        Assert.Null(content.Headers.ContentType);

        await AssertUnsupportedMediaTypeAsync(content);
    }

    private async Task AssertUnsupportedMediaTypeAsync(HttpContent content)
    {
        var response = await _client.PostAsync("/api/printer", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        // The whole body: no ProblemDetails field (title, traceId) and no Content-Type from the request.
        Assert.Equal(
            """{"success":false,"error":"Content-Type must be application/json","type":"validation"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("count=abc", "count: The value 'abc' is not valid.")]
    [InlineData("duration=1.5", "duration: The value '1.5' is not valid.")]
    // Over the int range.
    [InlineData("count=99999999999", "count: The value '99999999999' is not valid.")]
    [InlineData("count=", "count: The value '' is invalid.")]
    public async Task PostBeep_BadQueryValue_ReturnsPrintResponseShape(string query, string expectedError)
    {
        var response = await _client.PostAsync($"/api/printer/beep?{query}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PrintResponse>();
        Assert.Equal(new PrintResponse(false, expectedError, "validation"), body);
    }

    // The buzzer takes 1 to 9: a number outside it is clamped, not rejected. The closed loopback port answers 503.
    [Theory]
    [InlineData("count=0")]
    [InlineData("count=-5&duration=100")]
    [InlineData("count=2147483647")]
    public async Task PostBeep_NumberOutsideTheBuzzerRange_IsNotAValidationError(string query)
    {
        var response = await _client.PostAsync($"/api/printer/beep?{query}", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new PrintResponse(false, "Printer unreachable", "printer"), await response.Content.ReadFromJsonAsync<PrintResponse>());
    }

    [Theory]
    [InlineData(nameof(PrinterController.Print), 400)]
    [InlineData(nameof(PrinterController.Print), 415)]
    [InlineData(nameof(PrinterController.Beep), 400)]
    public void Action_ErrorStatus_IsDeclaredAsPrintResponse(string action, int status)
    {
        var declared = typeof(PrinterController).GetMethod(action)!
            .GetCustomAttributes(typeof(ProducesResponseTypeAttribute), false)
            .Cast<ProducesResponseTypeAttribute>()
            .Single(attribute => attribute.StatusCode == status);

        Assert.Equal(typeof(PrintResponse), declared.Type);
    }

    [Fact]
    public async Task PostPrinter_EmptyRequest_Returns400()
    {
        var body = await PostBadRequestAsync("{}");

        Assert.Equal(PrinterController.NoJobError, body.Error);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"Barcode","content":"BOX-0007","barcodeOptions":{"heightInDots":256}}]}""", "Block 0 (Barcode): barcodeOptions.heightInDots 256 is outside the range 1 to 255")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"feedLinesAfterPrint":-1}}""", "options.feedLinesAfterPrint -1 is outside the range 0 to 255")]
    [InlineData("""{"content":[{"type":"Text","content":"x"}],"options":{"defaultLineSpacing":256}}""", "options.defaultLineSpacing 256 is outside the range 0 to 255")]
    public async Task PostPrinter_NumberOutsideThePrinterRange_Returns400WithFieldAndRange(string json, string expectedError)
    {
        var body = await PostBadRequestAsync(json);

        Assert.Equal(expectedError, body.Error);
    }

    // The same answer as a barcode: a '?' in place of the character gives a code that scans to other data.
    [Theory]
    [InlineData("""{"content":[{"type":"QRCode","content":"SECRET\u001b@"}]}""", 0, 0x1B, 6)]
    [InlineData("""{"content":[{"type":"Text","content":"x"},{"type":"QRCode","content":"\u001dV\u0000"}]}""", 1, 0x1D, 0)]
    [InlineData("""{"content":[{"type":"QRCode","content":"a\tb"}]}""", 0, 0x09, 1)]
    [InlineData("""{"content":[{"type":"QRCode","content":"BEGIN:VCARD\r\nFN:A\rEND:VCARD"}]}""", 0, 0x0D, 17)]
    public async Task PostPrinter_ControlCharacterInQRCode_Returns400WithReason(string json, int block, int codePoint, int index)
    {
        var body = await PostBadRequestAsync(json);

        Assert.Equal($"Block {block} (QRCode): {PayloadErrorTests.QRCodeControl(codePoint, index)}", body.Error);
        Assert.DoesNotContain("SECRET", body.Error);
    }

    [Fact]
    public async Task PostPrinter_ControlCharacterInBarcode_Returns400WithReason()
    {
        var body = await PostBadRequestAsync("""{"content":[{"type":"Barcode","content":"AB\u001b@"}]}""");

        Assert.Equal("Block 0 (Barcode): a CODE128 barcode holds printable ASCII only", body.Error);
    }

    [Fact]
    public async Task McpPrint_ControlCharacterInQRCode_GivesTheSameReason()
    {
        var (_, text) = await _client.CallToolAsync("print", """{"content":[{"type":"QRCode","content":"SECRET\u001b@"}]}""");

        Assert.Equal($"Not printed: Block 0 (QRCode): {PayloadErrorTests.QRCodeControl(0x1B, 6)}", text);
    }

    [Fact]
    public async Task PostPrinter_JobOverThePaperLimit_Returns400()
    {
        var blocks = string.Join(',', Enumerable.Repeat("""{"type":"LineFeed","lines":100}""", 500));

        var body = await PostBadRequestAsync("{\"content\":[" + blocks + "]}");

        Assert.Equal(PayloadErrorTests.OverPaper(11, ContentType.LineFeed), body.Error);
    }
}
