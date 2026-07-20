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
    private readonly string _password;

    public LoginModel(IConfiguration config)
    {
        _password = config["Auth:Password"]
            ?? throw new InvalidOperationException("Missing Auth:Password");
    }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnPostAsync(string? password, string? returnUrl)
    {
        if (!FixedTimeEquals(password, _password))
        {
            ErrorMessage = "密碼錯誤";
            return Page();
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
