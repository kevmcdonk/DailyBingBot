﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.BotBuilderSamples.Bots;
using Microsoft.BotBuilderSamples.Dialogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WhereOnEarthBot.Controllers
{
    [Route("api/triggerchallenge")]
    [ApiController]
    public class TriggerChallengeController : ControllerBase
    {
        private readonly IBotFrameworkHttpAdapter _adapter;
        private readonly string _appId;
        private readonly string _appPassword;
        private readonly string _botId;
        private readonly string _botName;
        private readonly string _tenantId;
        private readonly string _teamsId;
        private readonly string _channelId;
        private readonly string _userId;
        private readonly string _serviceUrl;

        private readonly ConcurrentDictionary<string, ConversationReference> _conversationReferences;
        protected readonly BotState ConversationState;
        IConfiguration _configuration;
        ILogger<MainDialog> _logger;
        ICredentialProvider _credentialProvider;
        IBotTelemetryClient _telemetryClient;
        DialogBot<MainDialog> bot;

        public TriggerChallengeController(IBotFrameworkHttpAdapter adapter, IConfiguration configuration, ConversationState conversationState,
            ILogger<MainDialog> logger, IBotTelemetryClient telemetryClient,
            ConcurrentDictionary<string, ConversationReference> conversationReferences,
            ICredentialProvider credentialProvider, IBot bot)
        {
            this.bot = bot as DialogBot<MainDialog>;
            _adapter = adapter;
            _conversationReferences = conversationReferences;
            ConversationState = conversationState;
            _appId = configuration["MicrosoftAppId"];
            _appPassword = configuration["MicrosoftAppPassword"];
            _botId = configuration["BotId"];
            _botName = configuration["BotName"];
            
            _logger = logger;
            _configuration = configuration;
            _credentialProvider = credentialProvider;
            _telemetryClient = telemetryClient;

            // If the channel is the Emulator, and authentication is not in use,
            // the AppId will be null.  We generate a random AppId for this case only.
            // This is not required for production, since the AppId will have a value.
            if (string.IsNullOrEmpty(_appId))
            {
                _appId = Guid.NewGuid().ToString(); //if no AppId, use a random Guid
            }
        }

        public async Task<IActionResult> Get()
        {
           await this.bot.TriggerResultChat(this._adapter);

            // Let the caller know proactive messages have been sent
            return new ContentResult()
            {
                Content = "<html><body><h1>Proactive messages have been sent.</h1></body></html>",
                ContentType = "text/html",
                StatusCode = (int)HttpStatusCode.OK,
            };
        }
        private async Task BotCallback(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            try
            {
                var conversationStateAccessors = ConversationState.CreateProperty<DialogState>(nameof(DialogState));

                var dialogSet = new DialogSet(conversationStateAccessors);
                Dialog mainDialog = new MainDialog(_configuration, _logger, _telemetryClient);
                dialogSet.Add(mainDialog);

                var dialogContext = await dialogSet.CreateContextAsync(turnContext, cancellationToken);
                var results = await dialogContext.ContinueDialogAsync(cancellationToken);
                //if (results.Status == DialogTurnStatus.Empty)
                //{
                    await dialogContext.BeginDialogAsync(mainDialog.Id, null, cancellationToken);
                    await ConversationState.SaveChangesAsync(dialogContext.Context, false, cancellationToken);
                //}
                //else
                //    await turnContext.SendActivityAsync("Starting proactive message bot call back");
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex.Message);
            }
        }
    }
}