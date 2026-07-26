using System;
using System.Diagnostics;
using System.Windows.Input;

namespace Memo.UI;

internal sealed class SafeHyperlinkCommand : ICommand {
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => TryGetSafeUri(parameter, out _);

    public void Execute(object? parameter) {
        if (!TryGetSafeUri(parameter, out var uri)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static bool TryGetSafeUri(object? parameter, out Uri uri) {
        var value = parameter switch {
            Uri typed => typed.OriginalString,
            _ => parameter?.ToString(),
        };
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
