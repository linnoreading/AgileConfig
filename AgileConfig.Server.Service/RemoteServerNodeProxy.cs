﻿using Agile.Config.Protocol;
using AgileConfig.Server.Common;
using AgileConfig.Server.Data.Entity;
using AgileConfig.Server.Data.Freesql;
using AgileConfig.Server.IService;
using AgileHttp;
using AgileHttp.serialize;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgileConfig.Server.Service
{
    public class RemoteServerNodeProxy : IRemoteServerNodeProxy
    {
        internal class SerializeProvider : ISerializeProvider
        {
            public T Deserialize<T>(string content)
            {
                return JsonConvert.DeserializeObject<T>(content, new JsonSerializerSettings
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver()
                });
            }

            public string Serialize(object obj)
            {
                return JsonConvert.SerializeObject(obj);
            }
        }

        private IServerNodeService GetGerverNodeService()
        {
            return new ServerNodeService(new FreeSqlContext(FreeSQL.Instance));
        }

        private ILogger _logger;

        private ISysLogService GetSysLogService()
        {
            return new SysLogService(new FreeSqlContext(FreeSQL.Instance));
        }

        private static ConcurrentDictionary<string, ClientInfos> _serverNodeClientReports =
            new ConcurrentDictionary<string, ClientInfos>();

        public RemoteServerNodeProxy(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<RemoteServerNodeProxy>();
        }

        public async Task<bool> AllClientsDoActionAsync(string address, WebsocketAction action)
        {
            var result = await FunctionUtil.TRYAsync(async () =>
            {
                using (var resp = await (address + "/RemoteOP/AllClientsDoAction")
                    .AsHttp("POST", action)
                    .Config(new RequestOptions { ContentType = "application/json" })
                    .SendAsync())
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var result = resp.Deserialize<dynamic>();

                        if ((bool)result.success)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }, 5);

            using (var service = GetSysLogService())
            {
                await service.AddSysLogAsync(new SysLog
                {
                    LogTime = DateTime.Now,
                    LogType = result ? SysLogType.Normal : SysLogType.Warn,
                    LogText = $"通知节点【{address}】所有客户端：【{action.Action}】 响应：{(result ? "成功" : "失败")}"
                });
            }

            return result;
        }

        public async Task<bool> AppClientsDoActionAsync(string address, string appId, WebsocketAction action)
        {
            var result = await FunctionUtil.TRYAsync(async () =>
            {
                using (var resp = await (address + "/RemoteOP/AppClientsDoAction".AppendQueryString("appId", appId))
                    .AsHttp("POST", action)
                    .Config(new RequestOptions { ContentType = "application/json" })
                    .SendAsync())
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var result = resp.Deserialize<dynamic>();

                        if ((bool)result.success)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }, 5);

            using (var service = GetSysLogService())
            {
                await service.AddSysLogAsync(new SysLog
                {
                    LogTime = DateTime.Now,
                    LogType = result ? SysLogType.Normal : SysLogType.Warn,
                    AppId = appId,
                    LogText = $"通知节点【{address}】应用【{appId}】的客户端：【{action.Action}】 响应：{(result ? "成功" : "失败")}"
                });
            }

            return result;
        }

        public async Task<bool> OneClientDoActionAsync(string address, string clientId, WebsocketAction action)
        {
            var result = await FunctionUtil.TRYAsync(async () =>
            {
                using (var resp = await (address + "/RemoteOP/OneClientDoAction?clientId=" + clientId)
                    .AsHttp("POST", action)
                    .Config(new RequestOptions { ContentType = "application/json" })
                    .SendAsync())
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var result = resp.Deserialize<dynamic>();

                        if ((bool)result.success)
                        {
                            if (action.Action == ActionConst.Offline || action.Action == ActionConst.Remove)
                            {
                                if (_serverNodeClientReports.ContainsKey(address))
                                {
                                    if (_serverNodeClientReports[address].Infos != null)
                                    {
                                        var report = _serverNodeClientReports[address];
                                        report.Infos.RemoveAll(c => c.Id == clientId);
                                        report.ClientCount = report.Infos.Count;
                                    }
                                }
                            }

                            return true;
                        }
                    }

                    return false;
                }
            }, 5);

            using (var service = GetSysLogService())
            {
                await service.AddSysLogAsync(new SysLog
                {
                    LogTime = DateTime.Now,
                    LogType = result ? SysLogType.Normal : SysLogType.Warn,
                    LogText = $"通知节点【{address}】的客户端【{clientId}】：【{action.Action}】 响应：{(result ? "成功" : "失败")}"
                });
            }

            return result;
        }

        public async Task<ClientInfos> GetClientsReportAsync(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return new ClientInfos()
                {
                    ClientCount = 0,
                    Infos = new List<ClientInfo>()
                };
            }

            try
            {
                using (var resp = await (address + "/report/Clients").AsHttp()
               .Config(new RequestOptions(new SerializeProvider())).SendAsync())
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var clients = resp.Deserialize<ClientInfos>();
                        if (clients != null)
                        {
                            clients.Infos?.ForEach(i => { i.Address = address; });
                            return clients;
                        }
                    }

                    return new ClientInfos()
                    {
                        ClientCount = 0,
                        Infos = new List<ClientInfo>()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Try to get client infos from node {address} occur ERROR . ", ex);
            }

            return new ClientInfos()
            {
                ClientCount = 0,
                Infos = new List<ClientInfo>()
            };
        }

        public async Task TestEchoAsync(string address)
        {
            using var service = GetGerverNodeService();
            var node = await service.GetAsync(address);
            try
            {
                using var resp = await (node.Address + "/home/echo").AsHttp().SendAsync();
                if (resp.StatusCode == System.Net.HttpStatusCode.OK && (await resp.GetResponseContentAsync()) == "ok")
                {
                    node.LastEchoTime = DateTime.Now;
                    node.Status = Data.Entity.NodeStatus.Online;
                }
                else
                {
                    node.Status = Data.Entity.NodeStatus.Offline;
                }

                await service.UpdateAsync(node);
            }
            catch (Exception e)
            {
                _logger.LogInformation(e, "Try test node {0} echo , but fail .", node.Address);
            }
        }

        public Task TestEchoAsync()
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    using var service = GetGerverNodeService();
                    var nodes = await service.GetAllNodesAsync();

                    foreach (var node in nodes)
                    {
                        await TestEchoAsync(node.Address);
                    }

                    await Task.Delay(5000 * 1);
                }
            });
        }
    }
}