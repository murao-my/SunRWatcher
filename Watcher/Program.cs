using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

static void Fail(string msg)
{
    Console.Error.WriteLine("[ERROR] " + msg);
    Environment.Exit(1);
}

static double? ParseNumber(string s)
{
    var m = Regex.Match(s, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
    if (!m.Success) return null;
    if (double.TryParse(m.Value,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
    return null;
}

static async Task<double> ReadCurrentPriceAsync(string url)
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        Args = new[] { "--no-sandbox" }
    });

    var context = await browser.NewContextAsync(new()
    {
        UserAgent = "Mozilla/5.0 (compatible; SunriseWatcher/1.0)",
        BypassCSP = true,
        ViewportSize = new() { Width = 1366, Height = 920 }
    });

    var page = await context.NewPageAsync();
    Console.WriteLine($"[DEBUG] Navigating to: {url}");

    try
    {
        var resp = await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
        Console.WriteLine($"[DEBUG] Navigation response: {resp?.Status} {resp?.StatusText}");
        if (resp is null || !resp.Ok) Console.WriteLine("[WARN] initial navigation response not OK; page may still render via SPA.");
    }
    catch (Exception navEx)
    {
        Console.WriteLine($"[DEBUG] Navigation failed, trying with DOMContentLoaded: {navEx.Message}");
        // フォールバック: DOMContentLoadedで試行
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
    }

    // ---- 取り方1: “Current Price” セクションをテキストで拾う ----
    // 画面レンダリングを少し待つ
    Console.WriteLine("[DEBUG] Waiting for page to render...");
    await page.WaitForTimeoutAsync(8000);

    // 「Current Price」文字列の近辺を優先的に取得
    string? priceText = null;

    try
    {
        // 方法1: Current Priceラベルの下にある価格要素を直接取得
        var currentPriceLabel = page.Locator("text=Current Price").First;
        await currentPriceLabel.WaitForAsync(new() { Timeout = 15_000 });

        // Current Priceラベルの親要素を取得し、その中の価格テキストを探す
        var parentContainer = currentPriceLabel.Locator("xpath=..");
        var priceElement = parentContainer.Locator("span.text-2xl").First;

        if (await priceElement.CountAsync() > 0)
        {
            priceText = await priceElement.InnerTextAsync();
            Console.WriteLine($"[DEBUG] Found price via span.text-2xl: {priceText}");
        }
        else
        {
            // フォールバック: カード全体から取得
            var cardContainer = currentPriceLabel.Locator("xpath=ancestor::div[contains(@class, 'rounded-2xl')]");
            priceText = (await cardContainer.InnerTextAsync()).Replace("\n", " ").Trim();
            Console.WriteLine($"[DEBUG] Found price via card container: {priceText}");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"[DEBUG] Primary method failed: {e.Message}");
        // 取りこぼした場合はページ全体テキストからフォールバック
        priceText = (await page.ContentAsync());
    }

    // 想定表記: "4.74 USDrise per ATOM"
    var num = ParseNumber(priceText ?? "");
    if (num is null)
    {
        // さらにフォールバック：正規表現でより具体的に検索
        try
        {
            var pageContent = await page.ContentAsync();
            // より具体的なパターンで価格を検索
            var priceMatch = Regex.Match(pageContent, @"(\d+\.\d+)\s*USDrise\s*per\s*ATOM");
            if (priceMatch.Success)
            {
                num = ParseNumber(priceMatch.Groups[1].Value);
                Console.WriteLine($"[DEBUG] Found price via regex: {priceMatch.Groups[1].Value}");
            }
            else
            {
                // さらなるフォールバック：USDrise付近の要素を検索
                var near = await page.Locator("text=USDrise per ATOM").First.InnerTextAsync();
                num = ParseNumber(near);
                Console.WriteLine($"[DEBUG] Found price via USDrise locator: {near}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[DEBUG] All fallback methods failed: {e.Message}");
        }
    }

    if (num is null) Fail("Could not extract Current Price number from page.");
    return num ?? 0.0;
}

static async Task NotifyDiscordAsync(string webhookUrl, string url, double value, double low, double high)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var content = $"**Sunrise Current Price Alert**\n" +
                  $"- URL: {url}\n" +
                  $"- Current Price: `{value}`\n" +
                  $"- Thresholds: low=`{low}`, high=`{high}`\n" +
                  $"- Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}";

    var payload = new { content };
    var res = await http.PostAsync(webhookUrl,
        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    if (!res.IsSuccessStatusCode)
    {
        var body = await res.Content.ReadAsStringAsync();
        Fail($"Discord webhook failed: {(int)res.StatusCode} {body}");
    }
}

// メインプログラムの実行
var url = Environment.GetEnvironmentVariable("TARGET_URL");
var lowStr = Environment.GetEnvironmentVariable("THRESHOLD_LOW");
var highStr = Environment.GetEnvironmentVariable("THRESHOLD_HIGH");
var webhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");

if (string.IsNullOrWhiteSpace(lowStr) || string.IsNullOrWhiteSpace(highStr))
{
    Console.WriteLine("THRESHOLD_LOW / THRESHOLD_HIGH must be set");
    Fail("THRESHOLD_LOW / THRESHOLD_HIGH must be set");
}
if (string.IsNullOrWhiteSpace(webhook))
{
    Console.WriteLine("DISCORD_WEBHOOK_URL must be set");
    Fail("DISCORD_WEBHOOK_URL must be set");
}

if (!double.TryParse(lowStr, System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var low))
{
    Console.WriteLine("THRESHOLD_LOW must be numeric");
    Fail("THRESHOLD_LOW must be numeric");
}
if (!double.TryParse(highStr, System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var high))
{
    Console.WriteLine("THRESHOLD_HIGH must be numeric");
    Fail("THRESHOLD_HIGH must be numeric");
}

double price;
try
{
    price = await ReadCurrentPriceAsync(url);
}
catch (Exception e)
{
    Fail("Fetch/parse failed: " + e.Message);
    return;
}

Console.WriteLine($"[INFO] Current Price = {price}, low = {low}, high = {high}");

if (price <= low || price >= high || price != 0)
{
    Console.WriteLine("[INFO] out-of-range -> notifying Discord");
    await NotifyDiscordAsync(webhook, url, price, low, high);
}
else
{
    Console.WriteLine("[INFO] in-range -> no notification");
}
