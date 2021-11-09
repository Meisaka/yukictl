using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Net.Http;
using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Newtonsoft.Json;
using System.Reflection;
using System.Net;

namespace yukictl {
	class YukiToken {
		public string token;
		public string twitch_secret;
	}
	class TwitchToken {
		public string access_token;
		public string refresh_token;
		public long expires_in = 0;
		public List<string> scope = new List<string>();
		public string token_type;
	}
	class TwitchStreamInfo {
		public string id;
		public string user_id;
		public string user_login;
		public string user_name;
		public string game_id;
		public string game_name;
		public string type;
		public string title;
		public ulong viewer_count;
		public DateTime started_at;
		public string language;
		public string thumbnail_url;
		public List<string> tag_ids = new List<string>();
		public bool is_mature;
	}
	[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
	class TwitchAPIStreams {
		public List<TwitchStreamInfo> data;
		public Dictionary<string, string> pagination;
	}
	class YukiChannel {
		public string name;
		public bool allow_reee;
	}
	class YukiStreamState {
		public long last_time;
		public DateTime last_start;
	}
	class YukiStreamwatchState {
		public Dictionary<string, YukiStreamState> user_state = new Dictionary<string, YukiStreamState>();
		public TwitchToken api_token = new TwitchToken();
	}
	class YukiStreamwatch {
		public string twitch_client_id; // application public token
		public string notify_channel;
		[JsonIgnore]
		public SocketTextChannel notify_channel_obj;
		public string notify_role;
		[JsonIgnore]
		public string notify_role_obj;
		[JsonIgnore]
		public List<string> streamer_users = new List<string>();
		[JsonIgnore]
		public List<string> streamer_roles = new List<string>();
		[JsonProperty(Required = Required.Always, ObjectCreationHandling = ObjectCreationHandling.Replace)]
		public List<string> streamers { get {
				var rlist = streamer_roles.ToList();
				rlist.Concat(streamer_users);
				return rlist;
			} set {
				streamer_users.Clear();
				streamer_roles.Clear();
				foreach(var item in value) {
					if(item.StartsWith("@")) {
						streamer_roles.Add(item.Substring(1));
					} else {
						streamer_users.Add(item);
					}
				}
			} }
		public string message;
	}
	class YukiSettings {
		public string name;
		public string activity;
		public YukiStreamwatch streamwatch;
		public string led_host;
		public int led_port;
		public string led_channel;
		public List<ulong> led_messages;
		public List<YukiChannel> channels;
	}
	[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
	class YukiMessageState {
		[JsonProperty]
		public string link = null;
	}
	class YukiSerializableState {
		public Dictionary<ulong, YukiMessageState> my_messages = new Dictionary<ulong, YukiMessageState>();
		public YukiStreamwatchState streams = new YukiStreamwatchState();
	}
	class App {
		public static YukiSerializableState state = new YukiSerializableState();
		public static bool save_state = false;
		public static SocketTextChannel reee_channel = null;
		public static SocketTextChannel led_channel = null;
		public static IEmote emote1 = null;
		public static YukiSettings settings = null;
		public static Socket led_sock;
		public static bool update_leds = false;

		public const string state_file = "state.json";
		public const string settings_file = "settings.json";
		public static void SaveState() {
			File.WriteAllText(App.state_file, JsonConvert.SerializeObject(App.state));
			Console.WriteLine("Saved state");
			save_state = false;
		}
	}
	public class RequireRoleAttribute : PreconditionAttribute {
		private readonly string _name;
		public RequireRoleAttribute(string name) => _name = name;
		public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services) {
			// Check if this user is a Guild User, which is the only context where roles exist
			if(context.User is SocketGuildUser gUser) {
				// If this command was executed by a user with the appropriate role, return a success
				if(gUser.Roles.Any(r => r.Name == _name))
					return Task.FromResult(PreconditionResult.FromSuccess());
				else
					return Task.FromResult(PreconditionResult.FromError($"You must have a role named {_name} to run this command."));
			} else
				return Task.FromResult(PreconditionResult.FromError("You must be in a guild to run this command."));
		}
	}
	// Create a module with no prefix
	public class InfoModule : ModuleBase<SocketCommandContext> {
		// ~say hello world -> hello world
		[Command("say")]
		[RequireRole("REEEE")]
		[Summary("Echoes a message.")]
		public async Task SayAsync([Remainder][Summary("The text to echo")] string echo) {
			await ReplyAsync(echo);
		}

		[Command("reee")]
		[RequireRole("REEEE")]
		[Summary("EEEE?E?!.")]
		public async Task ReeeAsync() {
			var message = await App.reee_channel.SendMessageAsync("reeeeeeeee");
			App.state.my_messages.Add(message.Id, new YukiMessageState());
			await message.AddReactionAsync(App.emote1);
			App.save_state = true;
		}
	}

	class YukiActivity : IActivity {
		private string _name;
		public string Name => _name;
		public ActivityType Type => ActivityType.Watching;
		public ActivityProperties Flags => ActivityProperties.None;
		public string Details => "Yukiyukiyuki";
		public YukiActivity(string text) {
			_name = text;
		}
	}

	public class CommandHandler {
		private readonly DiscordSocketClient _client;
		private readonly CommandService _commands;

		// Retrieve client and CommandService instance via ctor
		public CommandHandler(DiscordSocketClient client, CommandService commands) {
			_commands = commands;
			_client = client;
		}

		public async Task InstallCommandsAsync() {
			// Hook the MessageReceived event into our command handler
			_client.MessageReceived += HandleCommandAsync;

			// Here we discover all of the command modules
			await _commands.AddModulesAsync(assembly: Assembly.GetEntryAssembly(),
											services: null);
		}

		private async Task HandleCommandAsync(SocketMessage messageParam) {
			// Don't process the command if it was a system message
			var message = messageParam as SocketUserMessage;
			if(message == null) return;

			// Create a number to track where the prefix ends and the command begins
			int argPos = 0;

			// Determine if the message is a command based on the prefix and make sure no bots trigger commands
			if(!(message.HasCharPrefix('~', ref argPos) ||
				message.HasMentionPrefix(_client.CurrentUser, ref argPos)) ||
				message.Author.IsBot)
				return;

			Console.WriteLine($"got a command message in {messageParam.Channel}");
			// Create a WebSocket-based command context based on the message
			var context = new SocketCommandContext(_client, message);

			// Execute the command with the command context we just
			// created, along with the service provider for precondition checks.
			await _commands.ExecuteAsync(
				context: context,
				argPos: argPos,
				services: null);
		}
	}
	class Program {
		private DiscordSocketClient _client;
		private CommandHandler _cmds;
		private SocketGuild _guild = null;
		private bool guild_downloaded = false;
		private HttpClient _webclient = null;
		private YukiToken _tokens;
		private int _led_num = 0;
		private HashSet<Cacheable<IUserMessage, ulong>> message_react = new HashSet<Cacheable<IUserMessage, ulong>>();
		private Task Log(LogMessage msg) {
			Console.WriteLine(msg.ToString());
			return Task.CompletedTask;
		}

		static void Main(string[] args) {
			new Program().AsyncMain().GetAwaiter().GetResult();
		}

		public Program() {
			_webclient = new HttpClient();
		}
		public async Task AsyncMain() {
			_tokens = JsonConvert.DeserializeObject<YukiToken>(File.ReadAllText("token.json"));
			App.settings = JsonConvert.DeserializeObject<YukiSettings>(File.ReadAllText(App.settings_file));
			if(File.Exists(App.state_file)) {
				App.state = JsonConvert.DeserializeObject<YukiSerializableState>(File.ReadAllText(App.state_file));
			}
			App.led_sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			App.led_sock.Connect(new IPEndPoint(IPAddress.Parse(App.settings.led_host), App.settings.led_port));

			await UpdateTwitchToken();

			_client = new DiscordSocketClient();
			_client.Log += Log;

			await _client.LoginAsync(TokenType.Bot, _tokens.token);
			_tokens.token = null;
			await _client.StartAsync();

			_client.UserIsTyping += UserTyping;
			_client.MessageUpdated += MessageUpdated;
			_client.ReactionAdded += ReactionAdded;
			_client.ReactionRemoved += ReactionRemoved;
			_client.Ready += Ready;
			_client.GuildMemberUpdated += MemberUpdated;
			_client.GuildUpdated += GuildUpdated;
			_client.GuildMembersDownloaded += MembersDownloaded;
			_client.GuildAvailable += GuildAvailable;
			await _client.SetActivityAsync(new YukiActivity(App.settings.activity));
			_cmds = new CommandHandler(_client, new CommandService());
			await _cmds.InstallCommandsAsync();

			// Block this task until the program is closed.
			while(true) {
				await Task.Delay(1000);
				if(_guild != null && !guild_downloaded) {
					guild_downloaded = true;
					await _guild.DownloadUsersAsync();
					Console.WriteLine($"Download complete?: {_guild.DownloadedMemberCount}");
					foreach(var channel in _guild.Channels) {
						if(channel is SocketTextChannel) {
							if(App.settings.channels.Exists(v => v.name == channel.Name)) {
								App.reee_channel = channel as SocketTextChannel;
								Console.WriteLine($"GuildChannel: {channel.Name}");
							}
							if(App.settings.led_channel == channel.Name) {
								App.led_channel = channel as SocketTextChannel;
							}
							if(App.settings.streamwatch.notify_channel == channel.Name) {
								App.settings.streamwatch.notify_channel_obj = channel as SocketTextChannel;
							}
						}
					}
					foreach(var role in _guild.Roles) {
						if(role.Name == App.settings.streamwatch.notify_role) {
							App.settings.streamwatch.notify_role_obj = role.Mention;
						}
						Console.WriteLine($"GuildRole: {role.Name}");
					}
					foreach(var emote in _guild.Emotes) {
						if(App.emote1 == null) App.emote1 = emote;
						Console.WriteLine($"GuildEmote: {emote.Name}");
					}
					foreach(var user in _guild.Users) {
						if(user.Activity != null) {
							Console.Write($"{user.Status}: ");
							foreach(var role in user.Roles) {
								Console.Write($"{role.Name}, ");
								
							}
							Console.Write($"activity {user.Activity.Type} {user.Activity.Name} //");
							Console.WriteLine();
							await CheckStreamingStatus(user);
						}
					}
					await SetupLedReactions();
				}
				if(App.save_state) {
					App.SaveState();
				}
				if(message_react.Count > 0) {
					var list = message_react.ToList();
					message_react.Clear();
					foreach(var item in list) {
						var message = await item.GetOrDownloadAsync();
						ProcessLinkMessage(message);
					}
				}
				if(App.update_leds) {
					await UpdateLeds();
					App.update_leds = false;
				}
			}
		}
		private async Task SetupLedReactions() {
			if(App.settings.led_messages == null) return;
			if(App.settings.led_messages.Count < 4) return;
			for(int i = 0; i < 4; i++) {
				var message = await App.led_channel.GetMessageAsync(App.settings.led_messages[i]);
				YukiMessageState mstate;
				if(App.state.my_messages.ContainsKey(message.Id)) {
					mstate =App.state.my_messages[message.Id];
				} else {
					mstate = new YukiMessageState();
					App.state.my_messages.Add(message.Id, mstate);
				}
				mstate.link = "leds" + i.ToString();
				if(message.Reactions.Count < 1) await message.AddReactionAsync(new Emoji("1\ufe0f\u20e3"));
				if(message.Reactions.Count < 2) await message.AddReactionAsync(new Emoji("2\ufe0f\u20e3"));
				if(message.Reactions.Count < 3) await message.AddReactionAsync(new Emoji("3\ufe0f\u20e3"));
				if(message.Reactions.Count < 4) await message.AddReactionAsync(new Emoji("4\ufe0f\u20e3"));
				if(message.Reactions.Count < 5) await message.AddReactionAsync(new Emoji("5\ufe0f\u20e3"));
				if(message.Reactions.Count < 6) await message.AddReactionAsync(new Emoji("6\ufe0f\u20e3"));
				if(message.Reactions.Count < 7 && (i & 1) == 0) await message.AddReactionAsync(new Emoji("7\ufe0f\u20e3"));
				ProcessLinkMessage(message as IUserMessage);
			}
			App.save_state = true;
		}
		public async Task UpdateLeds() {
			byte[] led_message = new byte[5];
			led_message[0] = ((byte)'S');
			// "left bar"
			// byte1: right 6 from right to left, and farest 1 left
			// byte2: left 6 from right to left
			// "right bar"
			// byte 1: left 6 from right to left
			// byte 2: right 7 from left to right
			uint led_int = (uint)_led_num;
			uint led_rev = led_int;
			led_rev = ((led_rev >> 16) & 0x0000ffff) | ((led_rev << 16) & 0xffff0000);
			led_rev = ((led_rev >> 8) & 0x00ff00ff) | ((led_rev << 8) & 0xff00ff00);
			led_rev = ((led_rev >> 4) & 0x0f0f0f0f) | ((led_rev << 4) & 0xf0f0f0f0);
			led_rev = ((led_rev >> 2) & 0x33333333) | ((led_rev << 2) & 0xcccccccc);
			led_rev = ((led_rev >> 1) & 0x55555555) | ((led_rev << 1) & 0xaaaaaaaa);
			led_message[1] = (byte)(((led_rev >> 19) & 0x3f) | ((led_int & 1) << 6));
			led_message[2] = (byte)((led_rev >> 25) & 0x3f);
			led_message[3] = (byte)((led_rev >> 13) & 0x3f);
			led_message[4] = (byte)((led_int >> 19) & 0x7f);
			await App.led_sock.SendAsync(new ArraySegment<byte>(led_message), SocketFlags.None);
		}
		private async Task UpdateTwitchToken() {
			long current_offset = DateTimeOffset.Now.ToUnixTimeSeconds();
			if(App.state.streams.api_token.expires_in - current_offset < 2) {
				var my_token = App.state.streams.api_token;
				my_token.access_token = null;
				App.save_state = true;
				UriBuilder uri = new UriBuilder("https://id.twitch.tv/oauth2/token");
				uri.Query = $"client_id={App.settings.streamwatch.twitch_client_id}&client_secret={_tokens.twitch_secret}&grant_type=client_credentials";
				var request = new HttpRequestMessage(HttpMethod.Post, uri.Uri);
				var response = await _webclient.SendAsync(request);
				if(!response.IsSuccessStatusCode) {
					Console.WriteLine($"Failed to query twitch for token {response.StatusCode}");
				}
				var token = JsonConvert.DeserializeObject<TwitchToken>(await response.Content.ReadAsStringAsync());
				my_token.access_token = token.access_token;
				my_token.expires_in = token.expires_in + DateTimeOffset.Now.ToUnixTimeSeconds();
				my_token.token_type = token.token_type;
				my_token.scope = token.scope;
				App.save_state = true;
				Console.WriteLine("Twitch token updated");
			}
		}
		private async Task<TwitchStreamInfo> GetStreamInfo(string uri) {
			var uri_in = new Uri(uri);
			if(uri_in.Host.Contains("twitch")) {
				await UpdateTwitchToken();
				string user = uri_in.LocalPath.Substring(1);
				Console.WriteLine($"Twitch lookup for {user}");
				var my_token = App.state.streams.api_token;
				if(my_token.access_token == null) {
					Console.WriteLine("Can not get stream info, no valid token");
					return null;
				}
				UriBuilder uri_out = new UriBuilder("https://api.twitch.tv/helix/streams");
				uri_out.Query = $"user_login={user}&first=1";
				var request = new HttpRequestMessage(HttpMethod.Get, uri_out.Uri);
				request.Headers.Add("Authorization", "Bearer " + my_token.access_token);
				request.Headers.Add("Client-ID", App.settings.streamwatch.twitch_client_id);
				var response = await _webclient.SendAsync(request);
				if(!response.IsSuccessStatusCode) {
					Console.WriteLine($"Failed to query twitch for token {response.StatusCode}");
				}
				var resp_string = await response.Content.ReadAsStringAsync();
				var resp_object = JsonConvert.DeserializeObject<TwitchAPIStreams>(resp_string);
				if(resp_object.data.Count > 0) {
					var stream = resp_object.data[0];
					return stream;
				}
			}
			return null;
		}
		private async Task CheckStreamingStatus(SocketGuildUser user) {
			if(user.Activity == null) return;
			if(!(user.Activity is StreamingGame)) return;
			string mention_string = App.settings.streamwatch.notify_role_obj;
			bool notify_this = false;
			foreach(var role in user.Roles) {
				if(App.settings.streamwatch.streamer_roles.Contains(role.Name)) {
					notify_this = true;
				}
			}
			if(!notify_this) return;
			Console.Write($"CSS-User: {user.Status}: ");
			Console.Write($"activity {user.Activity.Type} {user.Activity.Name} //");
			string user_shortname = user.Nickname;
			if(user_shortname == null) {
				user_shortname = user.Username;
			}
			var stream = user.Activity as StreamingGame;
			Console.Write($" {stream.Details} {stream.Url}//");
			Console.WriteLine();
			if(App.settings.streamwatch.notify_channel_obj != null) {
				long current_offset = DateTimeOffset.Now.ToUnixTimeMilliseconds();
				YukiStreamState yss = null;
				if(!App.state.streams.user_state.ContainsKey(user.Mention)) {
					yss = new YukiStreamState();
					App.state.streams.user_state.Add(user.Mention, yss);
					App.save_state = true;
				} else {
					yss = App.state.streams.user_state[user.Mention];
				}
				if(yss.last_time == 0 || (current_offset - yss.last_time > 6000)) {
					var stream_info = await GetStreamInfo(stream.Url);
					object[] args = { user_shortname, mention_string, stream_info.title, stream.Url, stream_info.game_name };
					var notify = string.Format(App.settings.streamwatch.message, args);
					// send message about the stream
					if(yss.last_start != stream_info.started_at) {
						var message = await App.settings.streamwatch.notify_channel_obj.SendMessageAsync(notify);
						yss.last_time = message.CreatedAt.ToUnixTimeMilliseconds();
						yss.last_start = stream_info.started_at;
						App.save_state = true;
					}
				}
			}
			
		}
		private Task GuildUpdated(SocketGuild before, SocketGuild after) {
			Console.WriteLine($"GuildUpdated: {after.Name}");
			return Task.CompletedTask;
		}

		private async Task GuildAvailable(SocketGuild guild) {
			Console.WriteLine($"GuildAvailable: {guild.Name}");
			_guild = guild;
		}

		private Task MembersDownloaded(SocketGuild guild) {
			Console.WriteLine($"Downloaded members for {guild.Name}");
			return Task.CompletedTask;
		}

		private async Task MemberUpdated(SocketGuildUser before, SocketGuildUser user) {
			Console.Write($"Member Update {before.Status} -> {user.Status} ");
			if(before.Activity == null || !(before.Activity is StreamingGame)) {
				await CheckStreamingStatus(user);
			}
			Console.WriteLine();
		}

		private Task Ready() {
			Console.WriteLine($"We are ready!");
			var guilds = _client.Guilds;
			foreach(var guild in guilds) {
				Console.WriteLine($"We are in \"{guild.Name}\", {guild.MemberCount} members");
			}
			return Task.CompletedTask;
		}

		private void UpdateLedReactions(IUserMessage message, int offset) {
			_led_num &= ~(((1 << message.Reactions.Count) - 1) << offset); // clear only our bits
			int led_index = 0;
			foreach(var pair in message.Reactions.AsEnumerable()) {
				if(led_index < 8) {
					_led_num |= (pair.Value.ReactionCount & 1) << (led_index + offset);
					led_index++;
				}
			}
			App.update_leds = true;
		}
		private void ProcessLinkMessage(IUserMessage message) {
			if(!App.state.my_messages.ContainsKey(message.Id)) return;
			var msg_state = App.state.my_messages[message.Id];
			if(msg_state.link == "leds0") {
				UpdateLedReactions(message, 0);
			}
			if(msg_state.link == "leds1") {
				UpdateLedReactions(message, 7);
			}
			if(msg_state.link == "leds2") {
				UpdateLedReactions(message, 13);
			}
			if(msg_state.link == "leds3") {
				UpdateLedReactions(message, 20);
			}
		}
		private async Task ReactionRemoved(Cacheable<IUserMessage, ulong> usermessage, ISocketMessageChannel channel, SocketReaction reaction) {
			if(App.state.my_messages.ContainsKey(usermessage.Id)) { // one of my messages?
				message_react.Add(usermessage);
			}
			//Console.WriteLine($"{channel.Name}: un-react: {message.Id} - {reaction.Emote.Name}");
		}

		private async Task ReactionAdded(Cacheable<IUserMessage, ulong> usermessage, ISocketMessageChannel channel, SocketReaction reaction) {
			if(App.state.my_messages.ContainsKey(usermessage.Id)) { // one of my messages?
				message_react.Add(usermessage);
			}
		}

		private async Task UserTyping(SocketUser user, ISocketMessageChannel channel) {
			Console.WriteLine($"{channel.Name}: Typing: {user.Status}");
		}

		private async Task MessageUpdated(Cacheable<IMessage, ulong> before, SocketMessage after, ISocketMessageChannel channel) {
			// If the message was not in the cache, downloading it will result in getting a copy of `after`.
			var message = await before.GetOrDownloadAsync();
			Console.WriteLine($"{message.Id} -> {after.EditedTimestamp}");
		}
	}
}
