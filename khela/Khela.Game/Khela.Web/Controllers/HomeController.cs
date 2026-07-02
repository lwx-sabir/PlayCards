using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Khela.Web.Models;

namespace Khela.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    // Sidebar placeholder pages — every non-Dashboard nav item lands here until its module is built.
    private static readonly Dictionary<string, string> Sections = new()
    {
        ["players"] = "Players",
        ["wallets"] = "Wallets & Ledger",
        ["leaderboards"] = "Leaderboards",
        ["tables"] = "Game Tables",
        ["reports"] = "Reports",
        ["settings"] = "Settings",
    };

    public IActionResult Soon(string section = "")
    {
        ViewData["Nav"] = section;   // matches the sidebar's active-item key
        ViewData["Section"] = Sections.TryGetValue(section ?? "", out var name) ? name : "Section";
        return View();
    }

    [AllowAnonymous]   // render the error page even for unauthenticated requests (no redirect loop under the Admin fallback)
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
