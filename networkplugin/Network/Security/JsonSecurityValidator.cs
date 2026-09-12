using System;
using System.Text;
using System.Text.Json;

namespace NetworkPlugin.Network.Security;

public static class JsonSecurityValidator
{
    public static bool Validate(string? json, int maxDepth, int maxStringLength, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        if (json!.Length > maxStringLength * 2)
        {
            errorMessage = $"JSON payload 长度超过安全上限: {json.Length} (limit={maxStringLength * 2})";
            return false;
        }

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var readerOptions = new JsonReaderOptions
            {
                MaxDepth = maxDepth > 0 ? maxDepth : 10,
                CommentHandling = JsonCommentHandling.Disallow
            };

            var reader = new Utf8JsonReader(bytes, readerOptions);
            while (reader.Read())
            {
                if (reader.CurrentDepth > readerOptions.MaxDepth)
                {
                    errorMessage = $"JSON 嵌套层级超过安全深度上限: depth={reader.CurrentDepth} (limit={readerOptions.MaxDepth})";
                    return false;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    string? str = reader.GetString();
                    if (str != null && str.Length > maxStringLength)
                    {
                        errorMessage = $"JSON 字段字符串长度超限: len={str.Length} (limit={maxStringLength})";
                        return false;
                    }
                }
            }

            return true;
        }
        catch (JsonException jex)
        {
            errorMessage = $"JSON 结构或深度验证失败: {jex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"JSON 验证未知异常: {ex.Message}";
            return false;
        }
    }
}
