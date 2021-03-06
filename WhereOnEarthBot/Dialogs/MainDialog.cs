// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.BotBuilderSamples;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Recognizers.Text.DataTypes.TimexExpression;
using WhereOnEarthBot.Models;
using WhereOnEarthBot.Services;
using System.Collections.Generic;
using WhereOnEarthBot.Helpers;


namespace Microsoft.BotBuilderSamples.Dialogs
{
    public class MainDialog : LogoutDialog
    {
        protected readonly IConfiguration Configuration;
        protected readonly ILogger Logger;
        private TableService tableService;

        public MainDialog(IConfiguration configuration, ILogger<MainDialog> logger, IBotTelemetryClient telemetryClient)
            : base(nameof(MainDialog), configuration["ConnectionName"])
        {
            Configuration = configuration;
            Logger = logger;
            TelemetryClient = telemetryClient;

            AddDialog(new OAuthPrompt(
                nameof(OAuthPrompt),
                new OAuthPromptSettings
                {
                    ConnectionName = ConnectionName,
                    Text = "Please login",
                    Title = "Login",
                    Timeout = 300000, // User has 5 minutes to login
                }));

            AddDialog(new TextPrompt(nameof(TextPrompt))
            {
                TelemetryClient = telemetryClient,
            });
            AddDialog(new ChallengeGuesserDialog(nameof(ChallengeGuesserDialog), configuration, logger, telemetryClient)
            {
                TelemetryClient = telemetryClient,
            });
            AddDialog(new WaterfallDialog(nameof(WaterfallDialog), new WaterfallStep[]
            {
                IntroStepAsync,
                ActStepAsync,
                FinalStepAsync
            })
            {
                TelemetryClient = telemetryClient,
            });

            // The initial child Dialog to run.
            InitialDialogId = nameof(WaterfallDialog);

            tableService = new TableService(Configuration["DailyChallengeTableConnectionString"], Configuration["DailyChallengeTableName"]);
        }

        private async Task<DialogTurnResult> IntroStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            TelemetryClient.TrackTrace("Main dialog started", Severity.Information, null);
            if (string.IsNullOrEmpty(Configuration["DailyChallengeTableConnectionString"]))
            {
                TelemetryClient.TrackTrace("Connection String not defined", Severity.Error, null);
                await stepContext.Context.SendActivityAsync(
                    MessageFactory.Text("NOTE: Storage Connection String is not configured. To continue, add 'DailyChallengeTableConnectionString' to the appsettings.json file."), cancellationToken);

                return await stepContext.EndDialogAsync(null, cancellationToken);
            }
            else
            {
                IMessageActivity reply = null;

                TelemetryClient.TrackTrace("Get Daily Challenge and Team Info", Severity.Information, null);
                DailyChallenge dailyChallenge = await tableService.GetDailyChallenge();
                DailyChallengeTeam team = await tableService.getDailyChallengeTeamInfo();
                TelemetryClient.TrackTrace("Check whether today's challenge exists", Severity.Information, null);
                if (dailyChallenge.photoUrl == null)
                {
                    TelemetryClient.TrackTrace("No Daily Challenge so check details", Severity.Information, null);
                    var activity = stepContext.Context.Activity;
                    if (team.ChannelData == null)
                    {
                        team.ChannelData = activity.GetChannelData<TeamsChannelData>();
                    }
                    var teamsChannelData = team.ChannelData;

                    var channelId = teamsChannelData.Channel.Id;
                    var tenantId = teamsChannelData.Tenant.Id;
                    string myBotId = activity.Recipient.Id;
                    string teamId = teamsChannelData.Team.Id;
                    string teamName = teamsChannelData.Team.Name;
                    
                    await this.tableService.SaveDailyChallengeTeamInfo(new DailyChallengeTeam()
                    {
                        ServiceUrl = activity.ServiceUrl,
                        TeamId = teamId,
                        TeamName = teamName,
                        TenantId = tenantId,
                        InstallerName = "Automatic",
                        BotId = myBotId,
                        ChannelId = channelId,
                        ChannelData = teamsChannelData
                    });

                    reply = MessageFactory.Attachment(new List<Attachment>());
                    Attachment attachment = null;

                    DailyChallengeInfo info = await GetInfo(stepContext);

                    if (info.currentSource == ImageSource.Google)
                    {
                        TelemetryClient.TrackTrace("Current source is Google so get an image", Severity.Information, null);
                        attachment = await GetGoogleImageChoiceAttachment();
                        TelemetryClient.TrackTrace("Loaded Google image", Severity.Information, null);
                    }
                    else
                    {
                        TelemetryClient.TrackTrace("Current source is Bing so get the latest image", Severity.Information, null);
                        int imageIndex = info.currentImageIndex;
                        attachment = await GetBingImageChoiceAttachment(imageIndex);
                        TelemetryClient.TrackTrace("Loaded Bing image", Severity.Information, null);
                    }

                    reply.Attachments.Add(attachment);
                    TelemetryClient.TrackTrace("Sending image reply", Severity.Information, null);
                    return await stepContext.PromptAsync(nameof(TextPrompt), new PromptOptions { Prompt = (Activity)reply }, cancellationToken);
                }
                else
                {
                    if (!dailyChallenge.resultSet)
                    {
                        // Pass on the check results message from the proactive controller if set
                        PromptOptions options = null;
                        if (stepContext != null && stepContext.Options != null)
                        {
                            options = (PromptOptions)stepContext.Options;
                            
                        }
                        return await stepContext.ReplaceDialogAsync(nameof(ChallengeGuesserDialog), options, cancellationToken);
                    }
                    else
                    {
                        IMessageActivity winningReply = MessageFactory.Attachment(new List<Attachment>());

                        winningReply.Attachments.Add(AttachmentHelper.ResultCardAttachment(dailyChallenge.winnerName, dailyChallenge.photoUrl, dailyChallenge.winnerGuess, dailyChallenge.distanceToEntry.ToString("#.##"), dailyChallenge.extractedLocation, dailyChallenge.text));
                        await stepContext.Context.SendActivityAsync(winningReply);
                        return await stepContext.EndDialogAsync(cancellationToken: cancellationToken);
                    }
                }
            }
        }

        private async Task<DialogTurnResult> ActStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var command = stepContext.Result.ToString();

            if (command.ToLower().Contains("choose image"))
            {
                int imageIndex = await GetImageIndex(stepContext);
                BingImageService imageService = new BingImageService();
                DailyChallengeImage image = await tableService.getDailyChallengeImage();
                BingMapService mapService = new BingMapService(Configuration["BingMapsAPI"]);
                Logger.LogInformation("Image Text: " + image.ImageText);
                DailyChallengeEntry challengeEntry = await mapService.GetLocationDetails(image.ImageText, Logger);

                if (challengeEntry == null)
                {
                    Logger.LogError("Unable to retrieve details of image");
                    throw new Exception("Unable to retrieve details from Google");
                }
                Logger.LogInformation("Image Response: " + challengeEntry.imageResponse);
                Logger.LogInformation("Longitude: " + challengeEntry.longitude);
                Logger.LogInformation("Latitude: " + challengeEntry.latitude);
                Logger.LogInformation("Latitude: " + challengeEntry.distanceFrom);

                var dailyChallenge = await tableService.GetDailyChallenge();

                dailyChallenge.photoUrl = image.Url;
                dailyChallenge.text = image.ImageText;
                dailyChallenge.latitude = challengeEntry.latitude;
                dailyChallenge.longitude = challengeEntry.longitude;
                dailyChallenge.extractedLocation = challengeEntry.imageResponse;
                dailyChallenge.entries = new List<DailyChallengeEntry>();
                dailyChallenge.publishedTime = DateTime.Now;
                dailyChallenge.currentStatus = DailyChallengeStatus.Guessing;
                await tableService.SaveDailyChallenge(dailyChallenge);

                IMessageActivity reply = MessageFactory.Attachment(new List<Attachment>());

                reply.Attachments.Add(AttachmentHelper.ImageChosen(dailyChallenge.photoUrl));
                var activity = (Activity)reply;
                
                await stepContext.Context.SendActivityAsync((Activity)reply);
                return await stepContext.EndDialogAsync(cancellationToken);
                //return await stepContext.ReplaceDialogAsync(nameof(ChallengeGuesserDialog), promptOptions, cancellationToken);
            }
            else if (command.ToLower().Contains("try another image"))
            {
                int imageIndex = await IncrementAndReturnImageIndex();
            }

            else if (command.ToLower().Contains("switch to google"))
            {
                try
                {
                    var reply = MessageFactory.Attachment(new List<Attachment>());
                    var attachment = await GetGoogleImageChoiceAttachment();
                    await UpdateImageSource(ImageSource.Google);
                    reply.Attachments.Add(attachment);
                }
                catch(Exception exp)
                {
                    Logger.LogError(exp, $"Could not set Google Image: {exp.Message} - {exp.StackTrace}", null);
                    throw exp;
                }
            }
            else if (command.ToLower().Contains("switch to bing"))
            {

                var reply = MessageFactory.Attachment(new List<Attachment>());
                int imageIndex = await GetImageIndex(stepContext);
                await UpdateImageSource(ImageSource.Bing);
                var attachment = await GetBingImageChoiceAttachment(imageIndex);
                // reply.AttachmentLayout = AttachmentLayoutTypes.Carousel;
                reply.Attachments.Add(attachment);
            }
            else
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text("Sorry, not sure about that"), cancellationToken);
            }

            return await stepContext.BeginDialogAsync(nameof(MainDialog), null, cancellationToken);
        }

        private async Task<DialogTurnResult> FinalStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            return await stepContext.EndDialogAsync(cancellationToken: cancellationToken);
        }

        private async Task<Attachment> GetBingImageChoiceAttachment(int imageIndex)
        {
            BingImageService imageService = new BingImageService();
            DailyChallengeImage image = imageService.GetBingImageUrl(imageIndex);
            await tableService.SaveDailyChallengeImage(image);

            var heroCard = new HeroCard
            {
                Title = "Today's Daily Challenge",
                Subtitle = image.ImageRegion,
                Text = "Click to choose the image for today or try another image.",
                Images = new List<CardImage> { new CardImage(image.Url) },
                Buttons = new List<CardAction> {
                        new CardAction(ActionTypes.ImBack, "Choose image", value: "Choose image"),
                        new CardAction(ActionTypes.ImBack, "Try another image", value: "Try another image"),
                        new CardAction(ActionTypes.ImBack, "Switch to Google", value: "Switch to Google")
                    }
            };

            return heroCard.ToAttachment();
        }

        private async Task<Attachment> GetGoogleImageChoiceAttachment()
        {
            GoogleMapService mapService = new GoogleMapService(Configuration["GoogleMapsAPI"]);
            HeroCard heroCard = null;

            try
            {


                DailyChallengeImage image = await mapService.GetRandomLocation();
                await tableService.SaveDailyChallengeImage(image);

                heroCard = new HeroCard
                {
                    Title = "Today's Daily Challenge",
                    Subtitle = image.ImageRegion,
                    Text = "Click to choose the image for today or try another image.",
                    Images = new List<CardImage> { new CardImage(image.Url) },
                    Buttons = new List<CardAction> {
                            new CardAction(ActionTypes.ImBack, "Choose image", value: "Choose image"),
                            new CardAction(ActionTypes.ImBack, "Try another Google image", value: "Try another image"),
                            new CardAction(ActionTypes.ImBack, "Switch to Bing", value: "Switch to Bing")
                        }
                };
            }
            catch(Exception exp)
            {
                if (exp.Message == "Sorry, couldn't find a suitable image. Try again shortly.")
                {
                    heroCard = new HeroCard
                    {
                        Title = "Today's Daily Challenge",
                        Subtitle = "Not found",
                        Text = "After trying 50 different locations, Google couldn't find a suitable image.",
                            Buttons = new List<CardAction> {
                            new CardAction(ActionTypes.ImBack, "Try another Google image", value: "Try another image"),
                            new CardAction(ActionTypes.ImBack, "Switch to Bing", value: "Switch to Bing")
                        }
                    };
                }
                else if (exp.Message == "Over Google query limit")
                {
                    heroCard = new HeroCard
                    {
                        Title = "Today's Daily Challenge",
                        Subtitle = "Not found",
                        Text = "The Google Maps Search Service is on a low level and has exceeeded it's usage. Please wait a few minutes and try again or switch to Bing.",
                        Buttons = new List<CardAction> {
                            new CardAction(ActionTypes.ImBack, "Try another Google image", value: "Try another image"),
                            new CardAction(ActionTypes.ImBack, "Switch to Bing", value: "Switch to Bing")
                        }
                    };
                }
                else
                {
                    throw exp;
                }
            }

            return heroCard.ToAttachment();
        }

        private async Task<Attachment> GetDailyChallengeImageAttachment()
        {
            DailyChallengeImage image = await tableService.getDailyChallengeImage();

            var heroCard = new HeroCard
            {
                Title = "Today's Daily Challenge",
                Subtitle = image.ImageRegion,
                Images = new List<CardImage> { new CardImage(image.Url) }
            };

            return heroCard.ToAttachment();
        }

        private async Task<DailyChallengeInfo> GetInfo(WaterfallStepContext context)
        {
            DailyChallengeInfo info = await tableService.GetLatestInfo();
            return info;
        }

        private async Task<int> GetImageIndex(WaterfallStepContext context)
        {
            DailyChallengeInfo info = await tableService.GetLatestInfo();
            return info.currentImageIndex;
        }

        private async Task<ImageSource> GetImageSource(WaterfallStepContext context)
        {
            DailyChallengeInfo info = await tableService.GetLatestInfo();
            return info.currentSource;
        }

        private async Task<DialogTurnResult> CommandStepAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            stepContext.Values["command"] = stepContext.Result;

            // Call the prompt again because we need the token. The reasons for this are:
            // 1. If the user is already logged in we do not need to store the token locally in the bot and worry
            // about refreshing it. We can always just call the prompt again to get the token.
            // 2. We never know how long it will take a user to respond. By the time the
            // user responds the token may have expired. The user would then be prompted to login again.
            //
            // There is no reason to store the token locally in the bot because we can always just call
            // the OAuth prompt to get the token or get a new token if needed.
            return await stepContext.BeginDialogAsync(nameof(OAuthPrompt), null, cancellationToken);
        }

        private async Task<int> IncrementAndReturnImageIndex()
        {
            DailyChallengeInfo info = await tableService.GetLatestInfo();
            info.currentImageIndex++;

            if (info.currentImageIndex > 7)
            {
                info.currentImageIndex = 0;
            }

            await tableService.SaveLatestInfo(info);

            return info.currentImageIndex;
        }

        private async Task<ImageSource> UpdateImageSource(ImageSource imageSource)
        {
            DailyChallengeInfo info = await tableService.GetLatestInfo();
            info.currentSource = imageSource;

            await tableService.SaveLatestInfo(info);

            return info.currentSource;
        }

        private async Task UpdateDailyChallengeImage(DailyChallengeImage image)
        {            
            await tableService.SaveDailyChallengeImage(image);

            return;
        }
    }
}
