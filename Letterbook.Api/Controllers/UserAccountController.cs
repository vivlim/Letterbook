﻿using System.Security.Claims;
using Letterbook.Api.Dto;
using Letterbook.Core;
using Letterbook.Core.Exceptions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Letterbook.Api.Controllers;

[Route("/api/v1/[controller]/[action]")]
public class UserAccountController : ControllerBase
{
    private readonly ILogger<UserAccountController> _logger;
    private readonly IAccountService _accountService;

    public UserAccountController(ILogger<UserAccountController> logger, IAccountService accountService)
    {
        _logger = logger;
        _accountService = accountService;
    }

    [HttpPost]
    public async Task<IActionResult> Login([FromBody]LoginRequest loginRequest)
    {
        try
        {
            var claims = await _accountService.AuthenticatePassword(loginRequest.Email, loginRequest.Password);
            if (!claims.Any()) return Unauthorized();

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
            return SignIn(principal, JwtBearerDefaults.AuthenticationScheme);
        }
        catch (RateLimitException e)
        {
            return StatusCode(429, new { e.Expiration, e.Message });
        }
    }
    
    [HttpPost]
    [Authorize]
    public IActionResult Logout()
    {
        var controller = nameof(Logout);
        _logger.LogInformation("{Controller}", controller);

        return SignOut();
    }
    
    [HttpPost]
    public async Task<IActionResult> Register([FromBody]RegistrationRequest registrationRequest)
    {
        var controller = nameof(Register);
        _logger.LogInformation("{Controller}", controller);

        try
        {
            var account = _accountService.RegisterAccount(registrationRequest.Email, registrationRequest.Handle, "password");
            
            // account created, now what?
        }
        catch (Exception e)
        {
            return BadRequest(e);
        }

        throw new NotImplementedException();
    }
}