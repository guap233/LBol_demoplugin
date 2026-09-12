using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace NetworkPlugin.Network.Server.Core;

public sealed class DefaultServerMessageCodec : IServerMessageCodec
{
        public bool TryDecode(NetDataReader reader, out string messageType, out string jsonPayload)
    {
        messageType = null;
        jsonPayload = string.Empty;

        try
        {
            if (reader == null || reader.AvailableBytes <= 0)
            {
                return false;
            }

            if (reader.AvailableBytes > NetworkConstants.MaxMessageSizeBytes)
            {
                Plugin.Logger?.LogWarning($"[安全拦截] 丢弃超大数据包: {reader.AvailableBytes} bytes (上限: {NetworkConstants.MaxMessageSizeBytes})");
                return false;
            }

            messageType = reader.GetString();
            if (string.IsNullOrWhiteSpace(messageType))
            {
                return false;
            }

            jsonPayload = reader.AvailableBytes > 0 ? reader.GetString() : string.Empty;

            if (reader.AvailableBytes > 0)
            {
                Plugin.Logger?.LogWarning($"[安全拦截] 消息包含未解析的尾随数据: {reader.AvailableBytes} bytes, type={messageType}");
                return false;
            }

            if (!string.IsNullOrEmpty(jsonPayload))
            {
                if (jsonPayload.Length > NetworkConstants.MaxStringLength * 2)
                {
                    Plugin.Logger?.LogWarning($"[安全拦截] 消息 payload 字符串超长: {jsonPayload.Length}");
                    return false;
                }

                if (!Security.JsonSecurityValidator.Validate(jsonPayload, NetworkConstants.MaxJsonDepth, NetworkConstants.MaxStringLength, out string jsonErr))
                {
                    Plugin.Logger?.LogWarning($"[安全拦截] 丢弃畸形/超深 JSON 消息: {jsonErr}");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"[安全拦截] 解码消息异常: {ex.Message}");
            return false;
        }
    }

        public void Encode(NetDataWriter writer, string messageType, string jsonPayload)
    {
        writer.Put(messageType ?? string.Empty);
        writer.Put(jsonPayload ?? string.Empty);
    }
}
