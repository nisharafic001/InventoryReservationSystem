using Inventory.Application.Auth;
using Inventory.Api.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>Obtain access tokens.</summary>
[ApiController]
[Route("api/v1/auth")]
[Tags("Authentication")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    /// <summary>Log in and receive a JWT access token.</summary>
    /// <remarks>
    /// Copy `accessToken` from the response, click **Authorize** (top right) and paste it to call protected endpoints.
    /// Limited to 10 attempts per minute per client.
    /// </remarks>
    /// <response code="200">Authenticated; use `accessToken` as a Bearer token until `expiresAtUtc`.</response>
    /// <response code="400">Username or password missing (`VALIDATION_FAILED`).</response>
    /// <response code="401">Wrong username or password (`INVALID_CREDENTIALS`).</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.LoginPolicy)]
    [Consumes("application/json")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Ok(await authService.LoginAsync(request, cancellationToken));
}
