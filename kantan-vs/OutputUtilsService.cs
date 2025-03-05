using Microsoft;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Helpers;

namespace Kantan;

#pragma warning disable VSEXTPREVIEW_OUTPUTWINDOW // Type is for evaluation purposes only and is subject to change or removal in future updates.
[VisualStudioContribution]
class OutputUtilsService : DisposableObject
{
    private static readonly string OutputPaneName = "KantanOutputWindowPane";

#pragma warning disable CA2213 // Disposable fields should be disposed, object now owned by this instance.
    private readonly VisualStudioExtensibility extensibility;
#pragma warning restore CA2213 // Disposable fields should be disposed

    private OutputWindow? outputWindow;

    public OutputUtilsService(VisualStudioExtensibility extensibility)
    {
        this.extensibility = Requires.NotNull(extensibility, nameof(extensibility));
    }

    public async Task WriteToOutputWindowAsync(string message, CancellationToken cancellationToken)
    {
        string displayNameResourceId = nameof(Strings.OutputPaneDisplayName);
        try
        {
            outputWindow ??= await extensibility.Views().Output.GetChannelAsync(
                OutputPaneName,
                displayNameResourceId,
                cancellationToken);
            await outputWindow.Writer.WriteLineAsync(message);
        }
        catch (StreamJsonRpc.RemoteInvocationException e)
        {
            // @todo: wtf is going on?
        }
    }
}
#pragma warning restore VSEXTPREVIEW_OUTPUTWINDOW // Type is for evaluation purposes only and is subject to change or removal in future updates.
