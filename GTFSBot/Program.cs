using GTFS;
using GTFS.Entities;
using GTFS.Entities.Enumerations;
using System;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace GTFSBot
{
    internal class Program
    {
        private static readonly TelegramBotClient BotClient = new(Environment.GetEnvironmentVariable("TOKEN") ?? string.Empty);
        private static GTFSFeed _gtfs;

        static async Task Main(string[] args)
        {
            // create the reader.
            var reader = new GTFSReader<GTFSFeed>();

            // read archive.
            _gtfs = reader.Read("gtfs.zip");
            //var a = GetMessage(new Location()
            //{
            //    Latitude = 59.3659931,
            //    Longitude = 24.6410702,
            //});
            await BotPreparing();
        }
        private static async Task BotPreparing()
        {
            using CancellationTokenSource cts = new();

            // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
            ReceiverOptions receiverOptions = new()
            {
                AllowedUpdates = Array.Empty<UpdateType>() // receive all update types
            };
            BotClient.StartReceiving(
                updateHandler: HandleUpdateAsync,
                pollingErrorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token
            );
            var me = await BotClient.GetMeAsync(cts.Token);
            Console.WriteLine($"Start listening for @{me.Username}");
            Console.ReadLine();
            // Send cancellation request to stop bot
            cts.Cancel();
        }

        private static Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(errorMessage);
            return Task.CompletedTask;
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                    if (update.Message?.Location != null)
                    {
                        var messageText = GetMessage(update.Message.Location);

                        // Respond to the user with the message
                        await botClient.SendTextMessageAsync(update.Message.Chat.Id, messageText, parseMode: ParseMode.Markdown);
                        return;
                    }
                    if (!string.IsNullOrEmpty(update.Message?.Text))
                    {
                        if (update.Message.Text.StartsWith("/start"))
                        {
                            await botClient.SendTextMessageAsync(update.Message.Chat.Id, "Hello, send me your location to recieve nearest stop times!", parseMode: ParseMode.Markdown);
                            return;
                        }
                        var locationParts = update.Message.Text.Split(',');
                        if (locationParts.Length != 2)
                        {
                            return;
                        }
                        double latitude, longitude;
                        if (!double.TryParse(locationParts[0], out latitude) || !double.TryParse(locationParts[1], out longitude))
                        {
                            return;
                        }
                        if (Geolocation.CoordinateValidator.Validate(latitude, longitude))
                        {
                            var messageText = GetMessage(new Location() 
                            {
                                Latitude = latitude,
                                Longitude = longitude
                            });
                            // Respond to the user with the message
                            await botClient.SendTextMessageAsync(update.Message.Chat.Id, messageText, parseMode: ParseMode.Markdown);
                            return;
                        }

                    }

                    break;
            }
        }

        private static string GetMessage(Location location)
        {
            var stops = _gtfs.Stops.ToList();
            var userLocation = new Geolocation.Coordinate(location.Latitude, location.Longitude);
            // Find the nearest stops
            var nearestStops = stops.Where(s => Geolocation.GeoCalculator.GetDistance(userLocation, new Geolocation.Coordinate(s.Latitude, s.Longitude), 1, Geolocation.DistanceUnit.Meters) <= 450).OrderBy(s => Geolocation.GeoCalculator.GetDistance(userLocation, new Geolocation.Coordinate(s.Latitude, s.Longitude), 1, Geolocation.DistanceUnit.Meters)).ToList();
            // Get the arrival times for the nearest stops
            var currentTime = TimeOfDay.FromDateTime(DateTime.Now);
            var maxTime = TimeOfDay.FromDateTime(DateTime.Now.AddHours(2));
            var arrivalTimes = nearestStops.SelectMany(s => _gtfs.StopTimes.Where(t => t.StopId.Equals(s.Id) && t.ArrivalTime > currentTime && t.ArrivalTime < maxTime).OrderBy(t => t.ArrivalTime)).ToList();
            // Get the trips for the arrival times
            var tripIds = arrivalTimes.Select(t => t.TripId).Distinct().ToList();
            var calendar = _gtfs.Calendars.ToList().Where(c => c.ContainsDay(DateTime.Now.DayOfWeek)).ToList();
            var trips = _gtfs.Trips.Where(t => tripIds.Contains(t.Id) && calendar.Any(c => c.ServiceId == t.ServiceId)).ToList();
            // Create a message with the list of public transport at the nearest stops and their arrival times
            var messageText = "Public transport near your location:\n\n";
            foreach (var stop in nearestStops)
            {
                messageText += $"🚏 {stop.Name}\n";
                var times = arrivalTimes.Where(t => t.StopId.Equals(stop.Id)).Take(20).ToList();
                foreach (var time in times)
                {
                    var trip = trips.FirstOrDefault(t => t.Id.Equals(time.TripId));
                    if (trip != null)
                    {
                        var route = _gtfs.Routes.ToList().Where(r => r.Id == trip.RouteId).FirstOrDefault();
                        if (route == null) continue;
                        messageText += $"  - {TypeToEmojiConverter(route.Type)} {route.ShortName} ({trip.Headsign}): {time.ArrivalTime.Value.ToString("HH:mm")}\n";
                    }
                }
                messageText += "\n";
            }

            return messageText;
        }

        private static string TypeToEmojiConverter(RouteTypeExtended type)
        {
            switch (type)
            {
                case RouteTypeExtended.BusService:
                    return "🚌";
                case RouteTypeExtended.TramService:
                    return "🚃";
                case RouteTypeExtended.TrolleybusService:
                    return "🚎";
                case RouteTypeExtended.WaterTransportService:
                    return "🚢";
                case RouteTypeExtended.RailwayService:
                    return "🚈";
                default:
                    return "❓";
            }
        }
    }
}