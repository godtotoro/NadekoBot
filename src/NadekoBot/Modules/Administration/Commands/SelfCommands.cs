﻿using Discord;
using Discord.Commands;
using NadekoBot.Attributes;
using NadekoBot.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Discord.WebSocket;
using NadekoBot.Services;
using NadekoBot.Services.Database.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using NadekoBot.DataStructures;
using NLog;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        [Group]
        public class SelfCommands : NadekoSubmodule
        {
            private static volatile bool _forwardDMs;
            private static volatile bool _forwardDMsToAllOwners;

            private static readonly object _locker = new object();

            private new static readonly Logger _log;

            static SelfCommands()
            {
                _log = LogManager.GetCurrentClassLogger();
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.BotConfig.GetOrCreate();
                    _forwardDMs = config.ForwardMessages;
                    _forwardDMsToAllOwners = config.ForwardToAllOwners;
                }

                var _ = Task.Run(async () =>
                {
                    while (!NadekoBot.Ready)
                        await Task.Delay(1000);

                    foreach (var cmd in NadekoBot.BotConfig.StartupCommands)
                    {
                        if (cmd.GuildId != null)
                        {
                            var guild = NadekoBot.Client.GetGuild((ulong) cmd.GuildId.Value);
                            var channel = guild?.GetChannel((ulong) cmd.ChannelId) as SocketTextChannel;
                            if (channel == null)
                                continue;

                            try
                            {
                                IUserMessage msg = await channel.SendMessageAsync(cmd.CommandText).ConfigureAwait(false);
                                msg = (IUserMessage)await channel.GetMessageAsync(msg.Id).ConfigureAwait(false);
                                await NadekoBot.CommandHandler.TryRunCommand(guild, channel, msg).ConfigureAwait(false);
                                //msg.DeleteAfter(5);
                            }
                            catch { }
                        }
                        await Task.Delay(400).ConfigureAwait(false);
                    }
                });
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [OwnerOnly]
            public async Task StartupCommandAdd([Remainder] string cmdText)
            {
                var guser = ((IGuildUser)Context.User);
                var cmd = new StartupCommand()
                {
                    CommandText = cmdText,
                    ChannelId = (long) Context.Channel.Id,
                    ChannelName = Context.Channel.Name,
                    GuildId = (long?) Context.Guild?.Id,
                    GuildName = Context.Guild?.Name,
                    VoiceChannelId = (long?) guser.VoiceChannel?.Id,
                    VoiceChannelName = guser.VoiceChannel?.Name,
                };
                using (var uow = DbHandler.UnitOfWork())
                {
                    uow.BotConfig
                       .GetOrCreate(set => set.Include(x => x.StartupCommands))
                       .StartupCommands.Add(cmd);
                    await uow.CompleteAsync().ConfigureAwait(false);
                }

                await Context.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                    .WithTitle(GetText("scadd"))
                    .AddField(efb => efb.WithName(GetText("server"))
                        .WithValue(cmd.GuildId == null ? $"-" : $"{cmd.GuildName}/{cmd.GuildId}").WithIsInline(true))
                    .AddField(efb => efb.WithName(GetText("channel"))
                        .WithValue($"{cmd.ChannelName}/{cmd.ChannelId}").WithIsInline(true))
                    .AddField(efb => efb.WithName(GetText("command_text"))
                        .WithValue(cmdText).WithIsInline(false)));
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [OwnerOnly]
            public async Task StartupCommands(int page = 1)
            {
                if (page < 1)
                    return;
                page -= 1;
                IEnumerable<StartupCommand> scmds;
                using (var uow = DbHandler.UnitOfWork())
                {
                    scmds = uow.BotConfig
                       .GetOrCreate(set => set.Include(x => x.StartupCommands))
                       .StartupCommands
                       .OrderBy(x => x.Id)
                       .ToArray();
                }
                scmds = scmds.Skip(page * 5).Take(5);
                if (!scmds.Any())
                {
                    await ReplyErrorLocalized("startcmdlist_none").ConfigureAwait(false);
                }
                else
                {
                    await Context.Channel.SendConfirmAsync("", string.Join("\n--\n", scmds.Select(x =>
                    {
                        string str = Format.Code(GetText("server")) + ": " + (x.GuildId == null ? "-" : x.GuildName + "/" + x.GuildId);

                        str += $@"
{Format.Code(GetText("channel"))}: {x.ChannelName}/{x.ChannelId}
{Format.Code(GetText("command_text"))}: {x.CommandText}";
                        return str;
                    })), footer: GetText("page", page + 1))
                         .ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task Wait(int miliseconds)
            {
                if (miliseconds <= 0)
                    return;
                Context.Message.DeleteAfter(0);
                try
                {
                    var msg = await Context.Channel.SendConfirmAsync($"⏲ {miliseconds}ms")
                   .ConfigureAwait(false);
                    msg.DeleteAfter(miliseconds / 1000);
                }
                catch { }

                await Task.Delay(miliseconds);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [OwnerOnly]
            public async Task StartupCommandRemove([Remainder] string cmdText)
            {
                StartupCommand cmd;
                using (var uow = DbHandler.UnitOfWork())
                {
                    var cmds = uow.BotConfig
                       .GetOrCreate(set => set.Include(x => x.StartupCommands))
                       .StartupCommands;
                    cmd = cmds
                       .FirstOrDefault(x => x.CommandText.ToLowerInvariant() == cmdText.ToLowerInvariant());

                    if (cmd != null)
                    {
                        cmds.Remove(cmd);
                        await uow.CompleteAsync().ConfigureAwait(false);
                    }
                }

                if (cmd == null)
                    await ReplyErrorLocalized("scrm_fail").ConfigureAwait(false);
                else
                    await ReplyConfirmLocalized("scrm").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [OwnerOnly]
            public async Task StartupCommandsClear()
            {
                using (var uow = DbHandler.UnitOfWork())
                {
                    uow.BotConfig
                       .GetOrCreate(set => set.Include(x => x.StartupCommands))
                       .StartupCommands
                       .Clear();
                    uow.Complete();
                }

                await ReplyConfirmLocalized("startcmds_cleared").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task ForwardMessages()
            {
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.BotConfig.GetOrCreate();
                    lock (_locker)
                        _forwardDMs = config.ForwardMessages = !config.ForwardMessages;
                    uow.Complete();
                }
                if (_forwardDMs)
                    await ReplyConfirmLocalized("fwdm_start").ConfigureAwait(false);
                else
                    await ReplyConfirmLocalized("fwdm_stop").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task ForwardToAll()
            {
                using (var uow = DbHandler.UnitOfWork())
                {
                    var config = uow.BotConfig.GetOrCreate();
                    lock (_locker)
                        _forwardDMsToAllOwners = config.ForwardToAllOwners = !config.ForwardToAllOwners;
                    uow.Complete();
                }
                if (_forwardDMsToAllOwners)
                    await ReplyConfirmLocalized("fwall_start").ConfigureAwait(false);
                else
                    await ReplyConfirmLocalized("fwall_stop").ConfigureAwait(false);

            }

            public static async Task HandleDmForwarding(IUserMessage msg, ImmutableArray<AsyncLazy<IDMChannel>> ownerChannels)
            {
                if (_forwardDMs && ownerChannels.Length > 0)
                {
                    var title = GetTextStatic("dm_from",
                                    NadekoBot.Localization.DefaultCultureInfo,
                                    typeof(Administration).Name.ToLowerInvariant()) +
                                $" [{msg.Author}]({msg.Author.Id})";

                    var attachamentsTxt = GetTextStatic("attachments",
                        NadekoBot.Localization.DefaultCultureInfo,
                        typeof(Administration).Name.ToLowerInvariant());

                    var toSend = msg.Content;

                    if (msg.Attachments.Count > 0)
                    {
                        toSend += $"\n\n{Format.Code(attachamentsTxt)}:\n" +
                                  string.Join("\n", msg.Attachments.Select(a => a.ProxyUrl));
                    }

                    if (_forwardDMsToAllOwners)
                    {
                        var allOwnerChannels = await Task.WhenAll(ownerChannels
                            .Select(x => x.Value))
                            .ConfigureAwait(false);

                        foreach (var ownerCh in allOwnerChannels.Where(ch => ch.Recipient.Id != msg.Author.Id))
                        {
                            try
                            {
                                await ownerCh.SendConfirmAsync(title, toSend).ConfigureAwait(false);
                            }
                            catch
                            {
                                _log.Warn("Can't contact owner with id {0}", ownerCh.Recipient.Id);
                            }
                        }
                    }
                    else
                    {
                            var firstOwnerChannel = await ownerChannels[0];
                            if (firstOwnerChannel.Recipient.Id != msg.Author.Id)
                            {
                                try
                                {
                                    await firstOwnerChannel.SendConfirmAsync(title, toSend).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }
                    }
                }


            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task ConnectShard(int shardid)
            {
                var shard = NadekoBot.Client.GetShard(shardid);

                if (shard == null)
                {
                    await ReplyErrorLocalized("no_shard_id").ConfigureAwait(false);
                    return;
                }
                try
                {
                    await ReplyConfirmLocalized("shard_reconnecting", Format.Bold("#" + shardid)).ConfigureAwait(false);
                    await shard.ConnectAsync().ConfigureAwait(false);
                    await ReplyConfirmLocalized("shard_reconnected", Format.Bold("#" + shardid)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn(ex);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task Leave([Remainder] string guildStr)
            {
                guildStr = guildStr.Trim().ToUpperInvariant();
                var server = NadekoBot.Client.GetGuilds().FirstOrDefault(g => g.Id.ToString() == guildStr) ??
                    NadekoBot.Client.GetGuilds().FirstOrDefault(g => g.Name.Trim().ToUpperInvariant() == guildStr);

                if (server == null)
                {
                    await ReplyErrorLocalized("no_server").ConfigureAwait(false);
                    return;
                }
                if (server.OwnerId != NadekoBot.Client.CurrentUser.Id)
                {
                    await server.LeaveAsync().ConfigureAwait(false);
                    await ReplyConfirmLocalized("left_server", Format.Bold(server.Name)).ConfigureAwait(false);
                }
                else
                {
                    await server.DeleteAsync().ConfigureAwait(false);
                    await ReplyConfirmLocalized("deleted_server", Format.Bold(server.Name)).ConfigureAwait(false);
                }
            }


            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task Die()
            {
                try
                {
                    await ReplyConfirmLocalized("shutting_down").ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
                await Task.Delay(2000).ConfigureAwait(false);
                Environment.Exit(0);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task SetName([Remainder] string newName)
            {
                if (string.IsNullOrWhiteSpace(newName))
                    return;

                await NadekoBot.Client.CurrentUser.ModifyAsync(u => u.Username = newName).ConfigureAwait(false);

                await ReplyConfirmLocalized("bot_name", Format.Bold(newName)).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task SetStatus([Remainder] SettableUserStatus status)
            {
                await NadekoBot.Client.SetStatusAsync(SettableUserStatusToUserStatus(status)).ConfigureAwait(false);

                await ReplyConfirmLocalized("bot_status", Format.Bold(status.ToString())).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task SetAvatar([Remainder] string img = null)
            {
                if (string.IsNullOrWhiteSpace(img))
                    return;

                using (var http = new HttpClient())
                {
                    using (var sr = await http.GetStreamAsync(img))
                    {
                        var imgStream = new MemoryStream();
                        await sr.CopyToAsync(imgStream);
                        imgStream.Position = 0;

                        await NadekoBot.Client.CurrentUser.ModifyAsync(u => u.Avatar = new Image(imgStream)).ConfigureAwait(false);
                    }
                }

                await ReplyConfirmLocalized("set_avatar").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task SetGame([Remainder] string game = null)
            {
                await NadekoBot.Client.SetGameAsync(game).ConfigureAwait(false);

                await ReplyConfirmLocalized("set_game").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task SetStream(string url, [Remainder] string name = null)
            {
                name = name ?? "";

                await NadekoBot.Client.SetGameAsync(name, url, StreamType.Twitch).ConfigureAwait(false);

                await ReplyConfirmLocalized("set_stream").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task Send(string where, [Remainder] string msg = null)
            {
                if (string.IsNullOrWhiteSpace(msg))
                    return;

                var ids = where.Split('|');
                if (ids.Length != 2)
                    return;
                var sid = ulong.Parse(ids[0]);
                var server = NadekoBot.Client.GetGuilds().FirstOrDefault(s => s.Id == sid);

                if (server == null)
                    return;

                if (ids[1].ToUpperInvariant().StartsWith("C:"))
                {
                    var cid = ulong.Parse(ids[1].Substring(2));
                    var ch = server.TextChannels.FirstOrDefault(c => c.Id == cid);
                    if (ch == null)
                    {
                        return;
                    }
                    await ch.SendMessageAsync(msg).ConfigureAwait(false);
                }
                else if (ids[1].ToUpperInvariant().StartsWith("U:"))
                {
                    var uid = ulong.Parse(ids[1].Substring(2));
                    var user = server.Users.FirstOrDefault(u => u.Id == uid);
                    if (user == null)
                    {
                        return;
                    }
                    await user.SendMessageAsync(msg).ConfigureAwait(false);
                }
                else
                {
                    await ReplyErrorLocalized("invalid_format").ConfigureAwait(false);
                    return;
                }
                await ReplyConfirmLocalized("message_sent").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task Announce([Remainder] string message)
            {
                var channels = NadekoBot.Client.GetGuilds().Select(g => g.DefaultChannel).ToArray();
                if (channels == null)
                    return;
                await Task.WhenAll(channels.Where(c => c != null).Select(c => c.SendConfirmAsync(GetText("message_from_bo", Context.User.ToString()), message)))
                        .ConfigureAwait(false);

                await ReplyConfirmLocalized("message_sent").ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task ReloadImages()
            {
                var time = await NadekoBot.Images.Reload().ConfigureAwait(false);
                await ReplyConfirmLocalized("images_loaded", time.TotalSeconds.ToString("F3")).ConfigureAwait(false);
            }

            private static UserStatus SettableUserStatusToUserStatus(SettableUserStatus sus)
            {
                switch (sus)
                {
                    case SettableUserStatus.Online:
                        return UserStatus.Online;
                    case SettableUserStatus.Invisible:
                        return UserStatus.Invisible;
                    case SettableUserStatus.Idle:
                        return UserStatus.AFK;
                    case SettableUserStatus.Dnd:
                        return UserStatus.DoNotDisturb;
                }

                return UserStatus.Online;
            }

            public enum SettableUserStatus
            {
                Online,
                Invisible,
                Idle,
                Dnd
            }
        }
    }
}