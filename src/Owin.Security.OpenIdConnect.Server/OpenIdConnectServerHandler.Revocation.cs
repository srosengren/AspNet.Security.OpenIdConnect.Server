/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json.Linq;
using Owin.Security.OpenIdConnect.Extensions;

namespace Owin.Security.OpenIdConnect.Server {
    internal partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        private async Task<bool> InvokeRevocationEndpointAsync() {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.LogError("The revocation request was rejected because an invalid " +
                                        "HTTP method was received: {Method}.", Request.Method);

                return await SendRevocationResponseAsync(null, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed revocation request has been received: " +
                                       "make sure to use either GET or POST."
                });
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrEmpty(Request.ContentType)) {
                Options.Logger.LogError("The revocation request was rejected because " +
                                        "the mandatory 'Content-Type' header was missing.");

                return await SendRevocationResponseAsync(null, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed revocation request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.LogError("The revocation request was rejected because an invalid 'Content-Type' " +
                                        "header was received: {ContentType}.", Request.ContentType);

                return await SendRevocationResponseAsync(null, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed revocation request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });
            }

            var request = new OpenIdConnectMessage(await Request.ReadFormAsync());

            if (string.IsNullOrWhiteSpace(request.Token)) {
                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed revocation request has been received: " +
                        "a 'token' parameter with an access or refresh token is required."
                });
            }

            // Insert the revocation request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // When client_id and client_secret are both null, try to extract them from the Authorization header.
            // See http://tools.ietf.org/html/rfc6749#section-2.3.1 and
            // http://openid.net/specs/openid-connect-core-1_0.html#ClientAuthentication
            if (string.IsNullOrEmpty(request.ClientId) && string.IsNullOrEmpty(request.ClientSecret)) {
                var header = Request.Headers.Get("Authorization");
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var value = header.Substring("Basic ".Length).Trim();
                        var data = Encoding.UTF8.GetString(Convert.FromBase64String(value));

                        var index = data.IndexOf(':');
                        if (index >= 0) {
                            request.ClientId = data.Substring(0, index);
                            request.ClientSecret = data.Substring(index + 1);
                        }
                    }

                    catch (FormatException) { }
                    catch (ArgumentException) { }
                }
            }

            var context = new ValidateRevocationRequestContext(Context, Options, request);
            await Options.Provider.ValidateRevocationRequest(context);

            if (context.IsRejected) {
                Options.Logger.LogError("The revocation request was rejected with the following error: {Error} ; {Description}",
                                        /* Error: */ context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                        /* Description: */ context.ErrorDescription);

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = context.ErrorDescription,
                    ErrorUri = context.ErrorUri
                });
            }

            // Ensure that the client_id has been set from the ValidateRevocationRequest event.
            else if (context.IsValidated && string.IsNullOrEmpty(request.ClientId)) {
                Options.Logger.LogError("The revocation request was validated but the client_id was not set.");

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "An internal server error occurred."
                });
            }

            AuthenticationTicket ticket = null;

            // Note: use the "token_type_hint" parameter to determine
            // the type of the token sent by the client application.
            // See https://tools.ietf.org/html/rfc7009#section-2.1
            switch (request.GetTokenTypeHint()) {
                case OpenIdConnectConstants.TokenTypeHints.AccessToken:
                    ticket = await DeserializeAccessTokenAsync(request.Token, request);
                    break;

                case OpenIdConnectConstants.TokenTypeHints.RefreshToken:
                    ticket = await DeserializeRefreshTokenAsync(request.Token, request);
                    break;
            }

            // Note: if the token can't be found using "token_type_hint",
            // the search must be extended to all supported token types.
            // See https://tools.ietf.org/html/rfc7009#section-2.1
            if (ticket == null) {
                ticket = await DeserializeAccessTokenAsync(request.Token, request) ??
                         await DeserializeRefreshTokenAsync(request.Token, request);
            }

            if (ticket == null) {
                Options.Logger.LogInformation("The revocation request was ignored because the token was invalid.");

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage());
            }

            // If the ticket is already expired, directly return a 200 response.
            else if (ticket.Properties.ExpiresUtc.HasValue &&
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Options.Logger.LogInformation("The revocation request was ignored because the token was already expired.");

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage());
            }

            // Note: unlike refresh tokens that can only be revoked by client applications,
            // access tokens can be revoked by either resource servers or client applications:
            // in both cases, the caller must be authenticated if the ticket is marked as confidential.
            if (context.IsSkipped && ticket.IsConfidential()) {
                Options.Logger.LogWarning("The revocation request was rejected because the caller was not authenticated.");

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest
                });
            }

            // When a client_id can be inferred from the revocation request,
            // ensure that the client application is an authorized presenter.
            if (!string.IsNullOrEmpty(request.ClientId) && ticket.HasPresenter() &&
                                                          !ticket.HasPresenter(request.ClientId)) {
                Options.Logger.LogWarning("The revocation request was rejected because the " +
                                          "refresh token was issued to a different client.");

                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest
                });
            }

            var notification = new HandleRevocationRequestContext(Context, Options, request, ticket);
            await Options.Provider.HandleRevocationRequest(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            if (!notification.Revoked) {
                return await SendRevocationResponseAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedTokenType,
                    ErrorDescription = "The token cannot be revoked."
                });
            }

            return await SendRevocationResponseAsync(request, new JObject());
        }

        private Task<bool> SendRevocationResponseAsync(OpenIdConnectMessage request, OpenIdConnectMessage response) {
            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload[parameter.Key] = parameter.Value;
            }

            return SendRevocationResponseAsync(request, payload);
        }

        private async Task<bool> SendRevocationResponseAsync(OpenIdConnectMessage request, JObject response) {
            if (request == null) {
                request = new OpenIdConnectMessage();
            }

            var notification = new ApplyRevocationResponseContext(Context, Options, request, response);
            await Options.Provider.ApplyRevocationResponse(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            return await SendPayloadAsync(response);
        }
    }
}
