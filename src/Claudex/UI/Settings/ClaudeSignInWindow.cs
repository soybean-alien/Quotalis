using Claudex.Core.Diagnostics;
using Claudex.Core.Plan;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Claudex.UI.Settings;

/// <summary>
/// claude.ai's own sign-in page in a small window, in the browser profile <see cref="ClaudeWebReader"/> reads with.
/// The user types their details into claude.ai directly; Claudex never sees them, saves no passwords or form data,
/// and only checks whether claude.ai's session cookie exists (never its value). Closes itself once signed in.
/// </summary>
internal sealed class ClaudeSignInWindow : Window
{
    private readonly WebView2 _view = new();
    private readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _signedIn;
    private bool _checking;

    public ClaudeSignInWindow()
    {
        Title = "Sign in to claude.ai for Claudex";
        var hint = new TextBlock
        {
            Text = "This is claude.ai's own page. Your sign-in stays on this PC, and this window closes once you're signed in. If Google sign-in refuses this window, use the email code.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 10, 16, 12),
            Opacity = 0.8,
            FontSize = 12,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(hint, 1);
        root.Children.Add(_view);
        root.Children.Add(hint);
        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 760));
        _check.Tick += async (_, _) => await CheckAsync();
        Closed += (_, _) =>
        {
            _check.Stop();
            _view.Close();
            Finished?.Invoke(_signedIn);
        };
        _ = StartAsync();
    }

    /// <summary>Raised once when the window closes: true when claude.ai signed the profile in.</summary>
    public event Action<bool>? Finished;

    private async Task StartAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(await ClaudeWebReader.EnvironmentAsync());
            var settings = _view.CoreWebView2.Settings;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            // A sign-in popup (Google) opens in this same window instead of a separate browser window.
            _view.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _view.CoreWebView2.Navigate(e.Uri);
            };
            _view.CoreWebView2.Navigate(ClaudeWebUsage.LoginUrl);
            _check.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("claude-web", "Sign-in window could not start the browser", new { error = ex.GetType().Name });
        }
    }

    private async Task CheckAsync()
    {
        if (_checking || _signedIn || _view.CoreWebView2 is null)
        {
            return;
        }

        _checking = true;
        try
        {
            var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync(ClaudeWebUsage.Origin);
            var onLogin = Uri.TryCreate(_view.CoreWebView2.Source, UriKind.Absolute, out var page)
                && page.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);
            if (!onLogin && cookies.Any(c => c.Name == ClaudeWebUsage.SessionCookie))
            {
                _signedIn = true;
                Log.Info("claude-web", "Signed in to claude.ai");
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("claude-web", "Sign-in check failed", new { error = ex.GetType().Name });
        }
        finally
        {
            _checking = false;
        }
    }
}
