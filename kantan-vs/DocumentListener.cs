namespace Kantan;

using System;
using System.Diagnostics;
using System.Security.Policy;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;

// @todo: feels weird that we want to track documents, but have to listen for text view events to get the changes needed.
// unsure if this is a hole in the api, or if intentional attempt to support case of a document being opened multiple times 
// and not having content always synced between those instances (basic testing shows the doc is synced between for example window splits).
// note: the fact that we get updates relating to cursor/selection change, without any change to the content, also makes it feel like
// it's not the correct solution.

[VisualStudioContribution]
internal class DocumentListener : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener
{
#pragma warning disable CA2213 // This is an extension scoped service.
    private readonly IDocumentTrackingProvider _trackingService;
    private readonly OutputUtilsService _outputService;
#pragma warning restore CA2213
    private DocumentPipeServer _pipeServer;
    private Task _noIdea;

    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo =
            [
                DocumentFilter.FromGlobPattern("**/*.decl", relativePath: false),
            ],
    };

    public DocumentListener(Extension extension, VisualStudioExtensibility extensibility, DocumentTrackingService trackingService, OutputUtilsService outputService)
        : base(extension, extensibility)
    {
        _trackingService = trackingService;
        _outputService = outputService;

        // @todo: don't know where this should live, but for now since we're defining the tracking service as scoped, meaning
        // it's instanced per extension part, seems to make sense to create the pipe server here.

        _pipeServer = new DocumentPipeServer(trackingService, outputService);
        _noIdea = _pipeServer.InstanceThreadAsync(CancellationToken.None); // @todo: cleanup of this member?
    }

    private Dictionary<Uri, uint> _documentRefCounts = new();

    // @todo: look into whether we need to make these handlers safe against possible concurrent calls

    public async Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        var uri = textView.Document.Uri;
        uint refCount;
        if (_documentRefCounts.TryGetValue(uri, out refCount))
        {
            _documentRefCounts[uri] = refCount + 1;
        }
        else
        {
            _documentRefCounts.Add(uri, 1);
            _trackingService.NotifyDocumentOpened(uri, textView.Document);
        }

        await _outputService.WriteToOutputWindowAsync(string.Format("Opened: {0}", textView.Uri), cancellationToken);
    }

    public async Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        var uri = textView.Document.Uri;
        var refCount = _documentRefCounts[uri];
        if (refCount == 1)
        {
            _trackingService.NotifyDocumentClosed(uri);
        }
        else
        {
            _documentRefCounts[uri] = refCount - 1;
        }

        await _outputService.WriteToOutputWindowAsync(string.Format("Closed: {0}", textView.Uri), cancellationToken);
    }

    public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        var uri = args.BeforeTextView.Document.Uri;

        if (args.Edits.Count > 0) // ignore selection-only changes
        {
            _trackingService.NotifyDocumentUpdated(uri, args.Edits, args.AfterTextView.Document);
        }

        await _outputService.WriteToOutputWindowAsync(string.Format("Edited: {0} ({1})", args.AfterTextView.Uri, args.Edits.ToString()), cancellationToken);
    }
}
