﻿using System.Security.Claims;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Mvc.Server.Controllers {
    public class UserinfoController : Controller {
        // Note: make sure to always specify ActiveAuthenticationSchemes = "oidc-server"
        // or use AutomaticAuthentication = true in the OpenID Connect server middleware options.
        [Authorize(ActiveAuthenticationSchemes = OpenIdConnectServerDefaults.AuthenticationScheme)]
        [HttpGet("/connect/userinfo")]
        public IActionResult Get() {
            return Json(new {
                sub = User.GetClaim(ClaimTypes.NameIdentifier),
                name = User.GetClaim(ClaimTypes.Name)
            });
        }
    }
}
