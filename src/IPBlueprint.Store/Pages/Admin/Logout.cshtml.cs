using IPBlueprint.Store.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IPBlueprint.Store.Pages.Admin;

public class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(AdminPolicy.Scheme);
        return RedirectToPage("/Index");
    }
}
