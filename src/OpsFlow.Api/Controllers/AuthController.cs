using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.Api.Http;
using OpsFlow.Application;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IOrganizationService organizations,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<CurrentUserDto>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Problem(title: "Invalid credentials", detail: "Email and password are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return UnauthorizedProblem();
        }

        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return UnauthorizedProblem();
        }

        var memberships = await organizations.ListMineAsync(user.Id, cancellationToken);
        return Ok(new CurrentUserDto(user.Id, user.Email ?? request.Email, user.DisplayName, memberships));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken cancellationToken)
    {
        var userId = ApiResults.RequireUserId(User);
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Unauthorized();
        }

        var memberships = await organizations.ListMineAsync(userId, cancellationToken);
        return Ok(new CurrentUserDto(user.Id, user.Email ?? "", user.DisplayName, memberships));
    }

    private ObjectResult UnauthorizedProblem()
        => Problem(title: "Invalid credentials", detail: "The email or password is incorrect.", statusCode: StatusCodes.Status401Unauthorized);

    public sealed record LoginRequest(string Email, string Password);
}
