using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using EmailTriage.App.Services;
using EmailTriage.App.ViewModels;
using EmailTriage.Core.Models;
using EmailTriage.Core.Services;

namespace EmailTriage.App.Views;

/// <summary>
/// The message box of the composer: a RichTextBox offering the formatting a
/// mail carries the same way everywhere - bold, italic, underline,
/// strikethrough, bullets, numbering, dashes, indents, links, @mentions and
/// pasted pictures, with the shadows Outlook's picture styles give them -
/// and nothing it does not. It keeps the view model's
/// plain-text mirror current and hands over the finished
/// <see cref="ComposeDocument"/> at send time.
/// </summary>
public sealed class ComposerEditor
{
    // A pasted picture is shown no wider than the box, never larger than itself.
    private const double MaxImageWidth = 600;
    private const double MaxImageHeight = 440;
    private const double WidestImage = 760;

    private readonly RichTextBox _box;
    private readonly ComposerViewModel _composer;

    // PNGs written for this message, cleared once it has gone.
    private readonly List<string> _files = new();

    // Where a paste began, so what came in can be made to look like the rest.
    private TextPointer? _pasteStart;
    private bool _syncing;

    /// <summary>Where the "@" of the mention being typed sits, so suggestions can drop beside it.</summary>
    public TextPointer? MentionStart { get; private set; }

    /// <summary>Raised as the caret moves, for the toolbar to show what is on at the caret.</summary>
    public event EventHandler? FormatChanged;

    public bool IsBold { get; private set; }
    public bool IsItalic { get; private set; }
    public bool IsUnderline { get; private set; }
    public bool IsStrikethrough { get; private set; }
    public bool IsBulleted { get; private set; }
    public bool IsNumbered { get; private set; }
    public bool IsDashed { get; private set; }

    public ComposerEditor(RichTextBox box, ComposerViewModel composer)
    {
        _box = box;
        _composer = composer;

        composer.ReadBody = () => Read(bakeShadows: true);
        composer.MentionInserted += OnMentionInserted;
        composer.LinkInserted += OnLinkInserted;
        composer.PropertyChanged += OnComposerChanged;

        box.TextChanged += OnTextChanged;
        box.SelectionChanged += (_, _) => UpdateFormatState();
        box.PreviewKeyDown += OnPreviewKeyDown;
        box.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(box, OnPasting);

        // What the mail cannot carry is not offered: alignment, sizes, super- and subscript.
        foreach (var command in new[]
                 {
                     EditingCommands.AlignCenter, EditingCommands.AlignRight, EditingCommands.AlignJustify, EditingCommands.AlignLeft,
                     EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
                     EditingCommands.ToggleSuperscript, EditingCommands.ToggleSubscript,
                 })
        {
            box.CommandBindings.Add(new CommandBinding(command,
                (_, e) => e.Handled = true,
                (_, e) => { e.CanExecute = false; e.Handled = true; }));
        }

        // Ctrl+B/I/U are the box's own. The rest follow Gmail and Outlook where
        // the app's composer keys leave room (Ctrl+Shift+L is "send later").
        Bind(Key.D8, ModifierKeys.Control | ModifierKeys.Shift, ToggleBullets);
        Bind(Key.D7, ModifierKeys.Control | ModifierKeys.Shift, ToggleNumbering);
        Bind(Key.D9, ModifierKeys.Control | ModifierKeys.Shift, ToggleDashes);
        Bind(Key.X, ModifierKeys.Control | ModifierKeys.Shift, ToggleStrikethrough);
        Bind(Key.OemCloseBrackets, ModifierKeys.Control, Indent);
        Bind(Key.OemOpenBrackets, ModifierKeys.Control, Outdent);
        Bind(Key.Space, ModifierKeys.Control, ClearFormatting);

        ComposeImageStore.Prune();
    }

    private void Bind(Key key, ModifierKeys modifiers, Action action) =>
        _box.InputBindings.Add(new KeyBinding(new RelayCommand(action), key, modifiers));

    // ---- formatting ---------------------------------------------------------

    public void ToggleBold() => Execute(EditingCommands.ToggleBold);
    public void ToggleItalic() => Execute(EditingCommands.ToggleItalic);
    public void ToggleUnderline() => Execute(EditingCommands.ToggleUnderline);
    public void ToggleBullets() => Execute(EditingCommands.ToggleBullets);
    public void ToggleNumbering() => Execute(EditingCommands.ToggleNumbering);
    public void Indent() => Execute(EditingCommands.IncreaseIndentation);
    public void Outdent() => Execute(EditingCommands.DecreaseIndentation);

    private void Execute(RoutedUICommand command)
    {
        command.Execute(null, _box);
        Done();
    }

    private void Done()
    {
        _box.Focus();
        UpdateFormatState();
    }

    /// <summary>WPF has no strikethrough command; it is toggled on the selection, leaving any underline alone.</summary>
    public void ToggleStrikethrough()
    {
        var current = _box.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var next = new TextDecorationCollection(
            current?.Where(d => d.Location != TextDecorationLocation.Strikethrough) ?? Enumerable.Empty<TextDecoration>());
        if (!Has(current, TextDecorationLocation.Strikethrough)) next.Add(TextDecorations.Strikethrough[0]);

        _box.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        Done();
    }

    public void ClearFormatting()
    {
        _box.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        _box.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        _box.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        Done();
    }

    /// <summary>
    /// A dash list is lines that start "- ", as one would type them: this puts
    /// the dash on each selected paragraph, or takes it off again. Enter
    /// continues the list.
    /// </summary>
    public void ToggleDashes()
    {
        var paragraphs = SelectedParagraphs().Where(p => p.Parent is not ListItem).ToList();
        if (paragraphs.Count == 0) return;

        var removing = ListMarker.Find(TextOf(paragraphs[0])) is { Numbered: false };
        foreach (var paragraph in paragraphs)
        {
            var marker = ListMarker.Find(TextOf(paragraph));
            if (removing)
            {
                if (marker is { } m) RemoveLeading(paragraph, m.Length);
            }
            else if (marker is null)
            {
                _ = new Run("- ", paragraph.ContentStart);
            }
        }
        Done();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = _box.Selection.Start;
        var end = _box.Selection.End;
        return AllParagraphs(_box.Document.Blocks)
            .Where(p => p.ContentEnd.CompareTo(start) >= 0 && p.ContentStart.CompareTo(end) <= 0);
    }

    private static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                        foreach (var p in AllParagraphs(item.Blocks)) yield return p;
                    break;
                case System.Windows.Documents.Section section:
                    foreach (var p in AllParagraphs(section.Blocks)) yield return p;
                    break;
            }
        }
    }

    /// <summary>Deletes the first <paramref name="chars"/> characters of a paragraph, whatever runs they sit in.</summary>
    private static void RemoveLeading(Paragraph paragraph, int chars)
    {
        var start = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var end = start;
        for (var i = 0; i < chars && end is not null; i++) end = end.GetNextInsertionPosition(LogicalDirection.Forward);
        if (end is null || end.CompareTo(paragraph.ContentEnd) > 0) end = paragraph.ContentEnd;
        new TextRange(start, end).Text = "";
    }

    private string TextOf(Paragraph paragraph) => ReadParagraph(paragraph, bakeShadows: false).Text;

    // ---- keys ----------------------------------------------------------------

    /// <summary>
    /// Enter at the end of a "- item" or "3. item" line starts the next one
    /// with the same dash or the next number; Enter on an empty item ends the
    /// list. WPF's own lists already behave this way.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || Keyboard.Modifiers != ModifierKeys.None || !_box.Selection.IsEmpty) return;

        var paragraph = _box.CaretPosition.Paragraph;
        if (paragraph is null || paragraph.Parent is ListItem) return;

        var text = TextOf(paragraph);
        if (ListMarker.Find(text) is not { } marker) return;

        if (ListMarker.IsEmptyItem(text))
        {
            RemoveLeading(paragraph, marker.Length);
            e.Handled = true;
            return;
        }

        var next = _box.CaretPosition.GetNextInsertionPosition(LogicalDirection.Forward);
        var atEnd = next is null || next.CompareTo(paragraph.ContentEnd) >= 0;
        if (!atEnd) return;

        e.Handled = true;
        EditingCommands.EnterParagraphBreak.Execute(null, _box);
        var run = new Run(marker.Leading + ListMarker.Next(marker), _box.CaretPosition);
        _box.CaretPosition = run.ContentEnd;
    }

    /// <summary>"*" then a space at the start of a line becomes a bullet, as in Outlook.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " ") return;

        var caret = _box.CaretPosition;
        var paragraph = caret.Paragraph;
        if (paragraph is null || paragraph.Parent is ListItem) return;
        if (caret.GetTextInRun(LogicalDirection.Backward) != "*" || TextOf(paragraph) != "*") return;

        e.Handled = true;
        RemoveLeading(paragraph, 1);
        EditingCommands.ToggleBullets.Execute(null, _box);
    }

    // ---- paste ----------------------------------------------------------------

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;

        // Files copied in Explorer go out as attachments, as dropped files do.
        if (!e.IsDragDrop && data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is string[] files)
            {
                e.CancelCommand();
                _composer.AddAttachments(files);
            }
            return;
        }

        if (TryReadImage(data, out var image))
        {
            e.CancelCommand();
            InsertImage(image);
            return;
        }

        // Rich text from elsewhere keeps its bold and lists but takes this box's font and colours.
        _pasteStart = _box.Selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward);
    }

    /// <summary>
    /// A picture on the clipboard - but not a copied table or web page, which
    /// carries a picture of itself beside its text: that pastes as text.
    /// </summary>
    private static bool TryReadImage(IDataObject data, out BitmapSource image)
    {
        image = null!;
        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text) || data.GetDataPresent(DataFormats.Rtf))
            return false;

        try
        {
            // PNG keeps transparency; the bitmap form loses it.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream png)
            {
                var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image = decoder.Frames[0];
                return true;
            }

            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                image = bitmap;
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException or ArgumentException)
        {
        }

        return false;
    }

    // ---- pictures ---------------------------------------------------------------

    /// <summary>What the editor knows about a picture in the message, kept on the Image's Tag.</summary>
    private sealed class PastedImage
    {
        public PastedImage(string path, BitmapSource bitmap)
        {
            Path = path;
            Bitmap = bitmap;
        }

        public string Path { get; }
        public BitmapSource Bitmap { get; }

        /// <summary>The shadow it wears; a picture that came in inside rich text has none until given one.</summary>
        public PictureShadow Shadow { get; set; } = PictureShadow.None;

        // The shadowed copy, for the size and shadow it was painted with.
        public (string Path, int Width, int Height)? Shadowed { get; set; }
        public (int Width, int Height, string Shadow) ShadowedFor { get; set; }
    }

    /// <summary>Puts a picture in at the caret, with the soft shadow, sized to fit the box.</summary>
    public void InsertImage(BitmapSource source)
    {
        string path;
        try { path = ComposeImageStore.Save(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _composer.Status = $"Could not keep that picture: {ex.Message}";
            return;
        }

        _files.Add(path);
        var image = new Image();
        Adopt(image, new PastedImage(path, ComposeImageStore.Load(path)));

        if (!_box.Selection.IsEmpty) _box.Selection.Text = "";
        var at = _box.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
        var container = new InlineUIContainer(image, at) { BaselineAlignment = BaselineAlignment.Bottom };
        _box.CaretPosition = container.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
        _box.Focus();
    }

    /// <summary>A picture file chosen from disk.</summary>
    public void InsertImageFile(string path)
    {
        try { InsertImage(ComposeImageStore.Load(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException)
        {
            _composer.Status = $"Could not read that picture: {ex.Message}";
        }
    }

    private void Adopt(Image image, PastedImage info)
    {
        image.Tag = info;
        image.Source = info.Bitmap;
        image.Stretch = Stretch.Uniform;
        image.Margin = new Thickness(0, 3, 0, 8);
        image.Cursor = Cursors.Arrow;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var (width, height) = Fit(info.Bitmap.PixelWidth, info.Bitmap.PixelHeight, MaxImageWidth, MaxImageHeight);
        image.Width = width;
        image.Height = height;
        SetShadow(image, PictureShadow.Default);
        image.ContextMenu = ImageMenu(image);
    }

    private static (double Width, double Height) Fit(double width, double height, double maxWidth, double maxHeight)
    {
        if (width <= 0 || height <= 0) return (1, 1);
        var scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return (Math.Round(width * scale), Math.Round(height * scale));
    }

    /// <summary>Gives the picture one of the shadows, on screen now and in the mail when it goes.</summary>
    private void SetShadow(Image image, PictureShadow style)
    {
        if (Info(image) is { } info) info.Shadow = style;
        image.Effect = ComposeImageStore.Shadow(style);
    }

    private PictureShadow ShadowOf(Image image) => (image.Tag as PastedImage)?.Shadow ?? PictureShadow.None;

    /// <summary>
    /// Right-click on a picture: its shadow, after Outlook's picture styles -
    /// none, soft, drop, slate drop and centre - its size, and removal.
    /// </summary>
    private ContextMenu ImageMenu(Image image)
    {
        var menu = new ContextMenu();

        var shadows = new MenuItem();
        var choices = new List<(MenuItem Item, PictureShadow Style)>();
        foreach (var style in PictureShadow.All)
        {
            var choice = new MenuItem { Header = style.Name, ToolTip = style.Hint, StaysOpenOnClick = false };
            choice.Click += (_, _) =>
            {
                SetShadow(image, style);
                ShowShadowChoice();
            };
            choices.Add((choice, style));
            shadows.Items.Add(choice);
        }
        ShowShadowChoice();
        menu.Items.Add(shadows);
        menu.Items.Add(new Separator());

        void ShowShadowChoice()
        {
            var current = ShadowOf(image);
            shadows.Header = $"Shadow: {current.Name}";
            foreach (var (item, style) in choices) item.IsChecked = style == current;
        }
        menu.Items.Add(Item("Smaller", () => Resize(image, 0.8)));
        menu.Items.Add(Item("Larger", () => Resize(image, 1.25)));
        menu.Items.Add(Item("Fit the message", () => Resize(image, 0)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Remove", () =>
        {
            if (image.Parent is InlineUIContainer container) container.SiblingInlines?.Remove(container);
        }));

        return menu;

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>Scales the picture by <paramref name="factor"/>, or back to its fitted size for 0. Never past its own pixels.</summary>
    private static void Resize(Image image, double factor)
    {
        if (image.Tag is not PastedImage info) return;

        var natural = (W: (double)info.Bitmap.PixelWidth, H: (double)info.Bitmap.PixelHeight);
        var (width, height) = factor <= 0
            ? Fit(natural.W, natural.H, MaxImageWidth, MaxImageHeight)
            : Fit(natural.W, natural.H, Math.Clamp(image.Width * factor, 40, Math.Min(natural.W, WidestImage)), double.MaxValue);

        image.Width = width;
        image.Height = height;
    }

    /// <summary>The picture's file, taking charge of one that arrived inside pasted rich text.</summary>
    private PastedImage? Info(Image image)
    {
        if (image.Tag is PastedImage info) return info;
        if (image.Source is not BitmapSource source) return null;

        try
        {
            var path = ComposeImageStore.Save(source);
            _files.Add(path);
            info = new PastedImage(path, source);
            image.Tag = info;
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- the document, read and written -------------------------------------------

    /// <summary>
    /// The message as a <see cref="ComposeDocument"/>. With
    /// <paramref name="bakeShadows"/> each shadowed picture is painted, with
    /// its shadow, into the file that goes out; without, the originals stand
    /// in, which is enough for the text mirror.
    /// </summary>
    public ComposeDocument Read(bool bakeShadows) => new(ReadBlocks(_box.Document.Blocks, bakeShadows));

    private List<ComposeBlock> ReadBlocks(BlockCollection blocks, bool bakeShadows)
    {
        var result = new List<ComposeBlock>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    result.Add(ReadParagraph(p, bakeShadows));
                    break;
                case List list:
                    var style = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                        or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman
                        ? ComposeListStyle.Number
                        : ComposeListStyle.Bullet;
                    result.Add(new ComposeList(style,
                        list.ListItems.Select(item => new ComposeListItem(ReadBlocks(item.Blocks, bakeShadows))).ToList()));
                    break;
                case System.Windows.Documents.Section section:
                    result.AddRange(ReadBlocks(section.Blocks, bakeShadows));
                    break;
                case BlockUIContainer container:
                    result.Add(new ComposeParagraph(ReadElement(container.Child, bakeShadows).ToList()));
                    break;
                case Table table:
                    // A pasted table: its cells, read in order, since the mail is not laid out as a table.
                    foreach (var group in table.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                result.AddRange(ReadBlocks(cell.Blocks, bakeShadows));
                    break;
            }
        }
        return result;
    }

    private ComposeParagraph ReadParagraph(Paragraph paragraph, bool bakeShadows)
    {
        var inlines = new List<ComposeInline>();
        ReadInlines(paragraph.Inlines, ComposeStyle.None, null, null, inlines, bakeShadows);
        return new ComposeParagraph(inlines) { IndentPt = Math.Max(0, paragraph.Margin.Left) * 0.75 };
    }

    private void ReadInlines(
        InlineCollection inlines, ComposeStyle inherited, string? url, ContactEntry? mention,
        List<ComposeInline> into, bool bakeShadows)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    if (run.Text.Length > 0)
                        into.Add(new ComposeRun(run.Text, inherited | StyleOf(run)) { Url = url, Mention = mention });
                    break;
                case LineBreak:
                    into.Add(new ComposeLineBreak());
                    break;
                case Hyperlink link:
                    // Its underline is the link's own, not the writer's.
                    var who = link.Tag as ContactEntry ?? mention;
                    var target = who is not null ? null : link.NavigateUri?.OriginalString ?? link.Tag as string ?? url;
                    ReadInlines(link.Inlines, inherited | WeightAndSlant(link), target, who, into, bakeShadows);
                    break;
                case Span span:
                    ReadInlines(span.Inlines, inherited | StyleOf(span), url, mention, into, bakeShadows);
                    break;
                case InlineUIContainer container:
                    into.AddRange(ReadElement(container.Child, bakeShadows));
                    break;
                case AnchoredBlock anchored:
                    foreach (var block in ReadBlocks(anchored.Blocks, bakeShadows))
                        if (block is ComposeParagraph p) into.AddRange(p.Inlines);
                    break;
            }
        }
    }

    private IEnumerable<ComposeInline> ReadElement(UIElement? element, bool bakeShadows)
    {
        if (element is not Image image || Info(image) is not { } info) yield break;

        var width = (int)Math.Round(double.IsNaN(image.Width) ? info.Bitmap.PixelWidth : image.Width);
        var height = (int)Math.Round(double.IsNaN(image.Height) ? info.Bitmap.PixelHeight : image.Height);

        if (info.Shadow.IsNone || !bakeShadows)
        {
            yield return new ComposeImage(info.Path, width, height);
            yield break;
        }

        if (info.Shadowed is not { } shadowed || info.ShadowedFor != (width, height, info.Shadow.Name))
        {
            shadowed = ComposeImageStore.WithShadow(info.Bitmap, width, height, info.Shadow);
            _files.Add(shadowed.Path);
            info.Shadowed = shadowed;
            info.ShadowedFor = (width, height, info.Shadow.Name);
        }

        yield return new ComposeImage(shadowed.Path, shadowed.Width, shadowed.Height);
    }

    private static ComposeStyle StyleOf(Inline inline)
    {
        var style = WeightAndSlant(inline);
        if (Has(inline.TextDecorations, TextDecorationLocation.Underline)) style |= ComposeStyle.Underline;
        if (Has(inline.TextDecorations, TextDecorationLocation.Strikethrough)) style |= ComposeStyle.Strikethrough;
        return style;
    }

    private static ComposeStyle WeightAndSlant(Inline inline)
    {
        var style = ComposeStyle.None;
        if (inline.FontWeight.ToOpenTypeWeight() >= 600) style |= ComposeStyle.Bold;
        if (inline.FontStyle != FontStyles.Normal) style |= ComposeStyle.Italic;
        return style;
    }

    private static bool Has(TextDecorationCollection? decorations, TextDecorationLocation location) =>
        decorations is not null && decorations.Any(d => d.Location == location);

    /// <summary>Replaces the message with <paramref name="document"/> - for an AI draft, or to start empty.</summary>
    public void Load(ComposeDocument document)
    {
        _syncing = true;
        try
        {
            var flow = _box.Document;
            flow.Blocks.Clear();
            foreach (var block in document.Blocks) flow.Blocks.Add(ToBlock(block));
            if (flow.Blocks.Count == 0) flow.Blocks.Add(new Paragraph());
            _box.CaretPosition = flow.ContentEnd;
        }
        finally { _syncing = false; }

        MentionStart = null;
        UpdateFormatState();
    }

    private Block ToBlock(ComposeBlock block) => block switch
    {
        ComposeParagraph p => ToParagraph(p),
        ComposeList l => ToList(l),
        _ => new Paragraph(),
    };

    private List ToList(ComposeList list)
    {
        var result = new List { MarkerStyle = list.Style == ComposeListStyle.Number ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
        foreach (var item in list.Items)
        {
            var listItem = new ListItem();
            foreach (var block in item.Blocks) listItem.Blocks.Add(ToBlock(block));
            if (listItem.Blocks.Count == 0) listItem.Blocks.Add(new Paragraph());
            result.ListItems.Add(listItem);
        }
        return result;
    }

    private Paragraph ToParagraph(ComposeParagraph p)
    {
        var paragraph = new Paragraph();
        if (p.IndentPt > 0) paragraph.Margin = new Thickness(p.IndentPt / 0.75, 0, 0, 0);

        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case ComposeRun r:
                    var run = new Run(r.Text);
                    if (r.Style.HasFlag(ComposeStyle.Bold)) run.FontWeight = FontWeights.Bold;
                    if (r.Style.HasFlag(ComposeStyle.Italic)) run.FontStyle = FontStyles.Italic;
                    var decorations = new TextDecorationCollection();
                    if (r.Style.HasFlag(ComposeStyle.Underline)) decorations.Add(TextDecorations.Underline[0]);
                    if (r.Style.HasFlag(ComposeStyle.Strikethrough)) decorations.Add(TextDecorations.Strikethrough[0]);
                    if (decorations.Count > 0) run.TextDecorations = decorations;

                    if (r.Mention is { } who) paragraph.Inlines.Add(MentionLink(run, who));
                    else if (r.Url is { } url) paragraph.Inlines.Add(Link(run, url));
                    else paragraph.Inlines.Add(run);
                    break;

                case ComposeLineBreak:
                    paragraph.Inlines.Add(new LineBreak());
                    break;

                case ComposeImage picture when File.Exists(picture.Path):
                    var image = new Image();
                    Adopt(image, new PastedImage(picture.Path, ComposeImageStore.Load(picture.Path)));
                    image.Width = picture.Width;
                    image.Height = picture.Height;
                    paragraph.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom });
                    break;
            }
        }

        return paragraph;
    }

    private static Hyperlink Link(Run run, string url, TextPointer? at = null)
    {
        var link = at is null ? new Hyperlink(run) : new Hyperlink(run, at);
        link.Tag = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        return link;
    }

    /// <summary>A mention looks like Outlook's: the name in the link colour, no underline.</summary>
    private static Hyperlink MentionLink(Run run, ContactEntry who, TextPointer? at = null)
    {
        var link = at is null ? new Hyperlink(run) : new Hyperlink(run, at);
        link.Tag = who;
        link.TextDecorations = new TextDecorationCollection();
        if (who.Address.Length > 0 && Uri.TryCreate("mailto:" + who.Address, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        return link;
    }

    // ---- mentions and links, written in by the view model --------------------------

    /// <summary>
    /// Called as the text or caret changes: the run before the caret is
    /// searched for an "@jan" being typed. Inside a finished mention or link
    /// nothing is searched.
    /// </summary>
    public void UpdateMentionSearch()
    {
        var caret = _box.CaretPosition;
        if (InsideHyperlink(caret))
        {
            MentionStart = null;
            _composer.UpdateMentionSearch("", 0);
            return;
        }

        var before = caret.GetTextInRun(LogicalDirection.Backward);
        var after = caret.GetTextInRun(LogicalDirection.Forward);
        _composer.UpdateMentionSearch(before + after, before.Length);

        MentionStart = _composer.SuggestingFor == RecipientField.Body
            ? caret.GetPositionAtOffset(-(before.Length - _composer.MentionStart))
            : null;
    }

    private static bool InsideHyperlink(TextPointer position)
    {
        for (var element = position.Parent as TextElement; element is not null; element = element.Parent as TextElement)
            if (element is Hyperlink) return true;
        return false;
    }

    private void OnMentionInserted(object? sender, MentionInsertion e)
    {
        var caret = _box.CaretPosition;
        var before = caret.GetTextInRun(LogicalDirection.Backward);
        var back = before.Length - e.Query.Start;
        var start = back >= 1 && back <= before.Length ? caret.GetPositionAtOffset(-back) ?? caret : caret;
        new TextRange(start, caret).Text = "";

        var link = MentionLink(new Run("@" + MentionText.NameOf(e.Contact)), e.Contact, start.GetInsertionPosition(LogicalDirection.Forward));
        _box.CaretPosition = AfterLink(link);
        MentionStart = null;
    }

    /// <summary>
    /// The selected text becomes the link - kept as it is, bold or not, when
    /// the link row left its wording alone. Text changed in the row, or a link
    /// with nothing selected, is written fresh at the caret.
    /// </summary>
    private void OnLinkInserted(object? sender, LinkInsertion e)
    {
        var selection = _box.Selection;
        if (!selection.IsEmpty && selection.Text.Trim() == e.Label && TryWrapSelection(e.Url) is { } wrapped)
        {
            _box.CaretPosition = AfterLink(wrapped);
            return;
        }

        if (!selection.IsEmpty) selection.Text = "";
        var link = Link(new Run(e.Label), e.Url, selection.Start.GetInsertionPosition(LogicalDirection.Forward));
        _box.CaretPosition = AfterLink(link);
    }

    /// <summary>
    /// Puts a link around what is selected, less any whitespace at its ends.
    /// Null when that cannot be done in place - a selection across paragraphs,
    /// or one that already holds a link - so the caller writes it afresh.
    /// </summary>
    private Hyperlink? TryWrapSelection(string url)
    {
        var start = _box.Selection.Start;
        var end = _box.Selection.End;
        if (start.Paragraph is null || start.Paragraph != end.Paragraph) return null;

        while (start.CompareTo(end) < 0 && IsWhitespace(start.GetTextInRun(LogicalDirection.Forward)))
            start = start.GetNextInsertionPosition(LogicalDirection.Forward) ?? end;
        while (end.CompareTo(start) > 0 && IsWhitespace(end.GetTextInRun(LogicalDirection.Backward), fromEnd: true))
            end = end.GetNextInsertionPosition(LogicalDirection.Backward) ?? start;
        if (start.CompareTo(end) >= 0) return null;

        if (new TextRange(start, end).Text.Length == 0 || InsideHyperlink(start) || InsideHyperlink(end)) return null;

        try
        {
            var link = new Hyperlink(start, end) { Tag = url };
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
            return link;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Something in the selection cannot sit inside a link; it is replaced instead.
            return null;
        }

        static bool IsWhitespace(string run, bool fromEnd = false) =>
            run.Length > 0 && char.IsWhiteSpace(fromEnd ? run[^1] : run[0]);
    }

    /// <summary>
    /// Where the caret goes after a link is written: after a space following
    /// it, added unless one is there already, so typing on does not extend the link.
    /// </summary>
    private static TextPointer AfterLink(Hyperlink link)
    {
        var after = link.ElementEnd.GetTextInRun(LogicalDirection.Forward);
        if (after.Length > 0 && char.IsWhiteSpace(after[0]))
            return link.ElementEnd.GetNextInsertionPosition(LogicalDirection.Forward) ?? link.ElementEnd;

        var space = new Run(" ", link.ElementEnd);
        return space.ContentEnd;
    }

    // ---- keeping the view model in step -----------------------------------------------

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (_pasteStart is { } start)
            {
                _pasteStart = null;
                Normalize(new TextRange(start, _box.CaretPosition));
            }

            var document = Read(bakeShadows: false);
            _composer.HasImages = document.HasImages;
            _composer.BodyText = document.PlainText;
        }
        finally { _syncing = false; }

        UpdateFormatState();
    }

    /// <summary>
    /// Pasted rich text arrives with its own fonts, colours and spacing; those
    /// are dropped so it reads like the rest, while bold, lists, links and
    /// pictures stay. Pictures inside it become the editor's own.
    /// </summary>
    private void Normalize(TextRange range)
    {
        for (var p = range.Start; p is not null && p.CompareTo(range.End) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart) continue;

            switch (p.GetAdjacentElement(LogicalDirection.Forward))
            {
                case InlineUIContainer { Child: Image image } when image.Tag is null:
                    if (Info(image) is { } info) Adopt(image, info);
                    break;

                case TextElement element:
                    element.ClearValue(TextElement.ForegroundProperty);
                    element.ClearValue(TextElement.BackgroundProperty);
                    element.ClearValue(TextElement.FontFamilyProperty);
                    element.ClearValue(TextElement.FontSizeProperty);
                    if (element is Paragraph paragraph)
                    {
                        paragraph.ClearValue(Block.MarginProperty);
                        paragraph.ClearValue(Block.PaddingProperty);
                        paragraph.ClearValue(Block.TextAlignmentProperty);
                        paragraph.ClearValue(Block.LineHeightProperty);
                        paragraph.ClearValue(Paragraph.TextIndentProperty);
                    }
                    break;
            }
        }
    }

    private void OnComposerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ComposerViewModel.BodyText):
                // The view model wrote the text itself (an AI draft, or a reset).
                if (_syncing || Read(bakeShadows: false).PlainText == _composer.BodyText) return;
                Load(ComposeDocument.FromPlainText(_composer.BodyText));
                break;

            case nameof(ComposerViewModel.IsOpen):
                if (!_composer.IsOpen)
                {
                    ComposeImageStore.Delete(_files);
                    _files.Clear();
                }
                Load(ComposeDocument.Empty);
                break;
        }
    }

    private void UpdateFormatState()
    {
        var selection = _box.Selection;
        IsBold = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 600;
        IsItalic = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle slant && slant != FontStyles.Normal;
        var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        IsUnderline = Has(decorations, TextDecorationLocation.Underline);
        IsStrikethrough = Has(decorations, TextDecorationLocation.Strikethrough);

        var paragraph = selection.Start.Paragraph;
        var list = (paragraph?.Parent as ListItem)?.Parent as List;
        IsBulleted = list is { MarkerStyle: TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box };
        IsNumbered = list is not null && !IsBulleted;
        IsDashed = list is null && paragraph is not null && ListMarker.Find(TextOf(paragraph)) is { Numbered: false };

        FormatChanged?.Invoke(this, EventArgs.Empty);
    }
}
