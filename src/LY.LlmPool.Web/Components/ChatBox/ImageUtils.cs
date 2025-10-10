using Microsoft.AspNetCore.Components.Forms;

namespace LY.LlmPool.Web.Components.ChatHelpers;

/// <summary>
/// 图片处理工具类
/// </summary>
public static class ImageUtils
{
    /// <summary>
    /// 选中的图片信息
    /// </summary>
    public class SelectedImage
    {
        public string FileName { get; set; } = string.Empty;
        public string DataUrl { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string MimeType { get; set; } = string.Empty;
    }

    /// <summary>
    /// 图片上传验证结果
    /// </summary>
    public class UploadValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }

        public static UploadValidationResult Success() => new() { IsValid = true };
        public static UploadValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    /// <summary>
    /// 验证图片上传
    /// </summary>
    public static UploadValidationResult ValidateImageUpload(IBrowserFile file, int maxSizeMB = 5, int maxCount = 5, List<SelectedImage>? existingImages = null)
    {
        // 检查文件大小
        if (file.Size > maxSizeMB * 1024 * 1024)
        {
            return UploadValidationResult.Error($"图片 {file.Name} 超过{maxSizeMB}MB限制");
        }

        // 检查图片数量
        var currentCount = existingImages?.Count ?? 0;
        if (currentCount >= maxCount)
        {
            return UploadValidationResult.Error($"最多只能上传{maxCount}张图片");
        }

        // 检查文件类型
        if (!file.ContentType.StartsWith("image/"))
        {
            return UploadValidationResult.Error("只允许上传图片文件");
        }

        return UploadValidationResult.Success();
    }

    /// <summary>
    /// 验证图片上传（基于文件大小与文件名的重载，方便与 Upload 组件配合使用）
    /// </summary>
    public static UploadValidationResult ValidateImageUpload(long fileSize, string fileName, int maxSizeMB = 5, int maxCount = 5, List<SelectedImage>? existingImages = null, string? contentType = null)
    {
        // 检查文件大小
        if (fileSize > maxSizeMB * 1024L * 1024L)
        {
            return UploadValidationResult.Error($"图片 {fileName} 超过{maxSizeMB}MB限制");
        }

        // 检查图片数量
        var currentCount = existingImages?.Count ?? 0;
        if (currentCount >= maxCount)
        {
            return UploadValidationResult.Error($"最多只能上传{maxCount}张图片");
        }

        // 如果提供了 contentType，则验证类型
        if (!string.IsNullOrEmpty(contentType) && !contentType.StartsWith("image/"))
        {
            return UploadValidationResult.Error("只允许上传图片文件");
        }

        return UploadValidationResult.Success();
    }

    /// <summary>
    /// 处理图片文件转换为 SelectedImage
    /// </summary>
    public static async Task<SelectedImage?> ProcessImageFileAsync(IBrowserFile file, long maxSizeBytes = 10 * 1024 * 1024)
    {
        try
        {
            // 再次检查文件大小
            if (file.Size > maxSizeBytes)
            {
                return null;
            }

            // 读取文件数据
            using var stream = file.OpenReadStream(maxAllowedSize: maxSizeBytes);
            using var ms = new MemoryStream();
            // 使用 CopyToAsync 保证不会触发 CA2022 的不安全 ReadAsync 用法
            await stream.CopyToAsync(ms);
            var buffer = ms.ToArray();

            // 转换为 Base64 数据 URL
            var base64String = Convert.ToBase64String(buffer);
            var dataUrl = $"data:{file.ContentType};base64,{base64String}";

            return new SelectedImage
            {
                FileName = file.Name,
                DataUrl = dataUrl,
                Data = buffer,
                MimeType = file.ContentType
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取图片显示尺寸（用于预览）
    /// </summary>
    public static (int Width, int Height) GetPreviewSize(byte[] imageData, int maxWidth = 300, int maxHeight = 300)
    {
        try
        {
            // 这里可以集成图片处理库来获取实际尺寸
            // 暂时返回固定预览尺寸
            return (maxWidth, maxHeight);
        }
        catch
        {
            return (maxWidth, maxHeight);
        }
    }
}