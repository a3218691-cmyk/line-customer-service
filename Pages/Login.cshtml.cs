using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LineBotLogger.Pages;

public class LoginModel : PageModel
{
    // 全域防暴力破解:錯 5 次鎖 10 分鐘(單一密碼、單機,static 即可)
    private static readonly object Gate = new();
    private static int _failCount;
    private static DateTime _lockedUntil;

    private readonly string _password;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IConfiguration config, ILogger<LoginModel> logger)
    {
        _password = config["Auth:Password"]
            ?? throw new InvalidOperationException("Missing Auth:Password");
        _logger = logger;
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnPostAsync(string? password, string? returnUrl)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow < _lockedUntil)
            {
                ErrorMessage = "嘗試次數過多,請稍後再試";
                return Page();
            }
        }

        if (!FixedTimeEquals(password, _password))
        {
            lock (Gate)
            {
                _failCount++;
                _logger.LogWarning("Login failed, attempt {Count}", _failCount);
                if (_failCount >= 5)
                {
                    _lockedUntil = DateTime.UtcNow.AddMinutes(10);
                    _failCount = 0;
                    _logger.LogWarning("Login locked until {Until:u}", _lockedUntil);
                }
            }
            await Task.Delay(1000); // 拖慢暴力嘗試
            ErrorMessage = "密碼錯誤";
            return Page();
        }

        lock (Gate)
        {
            _failCount = 0;
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin") },
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        // 只接受站內相對路徑,避免 open redirect
        return Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : RedirectToPage("/Index");
    }

    private static bool FixedTimeEquals(string? input, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(input ?? ""), Encoding.UTF8.GetBytes(expected));
}
