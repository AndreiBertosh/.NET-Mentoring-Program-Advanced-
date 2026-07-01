using Microsoft.AspNetCore.Mvc.Formatters;
using ProtoBuf.Meta;

namespace ReplicationDemo.Api.Formatters;

/// <summary>
/// ASP.NET Core output formatter for <c>application/x-protobuf</c>.
/// Uses protobuf-net's <see cref="RuntimeTypeModel"/> to serialise any type that
/// carries <c>[ProtoContract]</c> / <c>[ProtoMember]</c> attributes.
///
/// Registered after the built-in JSON formatter so that JSON remains the default for
/// clients sending <c>Accept: */*</c>. Only clients that explicitly send
/// <c>Accept: application/x-protobuf</c> receive binary Protobuf.
/// </summary>
public sealed class ProtobufOutputFormatter : OutputFormatter
{
    public const string MediaType = "application/x-protobuf";

    public ProtobufOutputFormatter()
    {
        SupportedMediaTypes.Add(MediaType);
    }

    protected override bool CanWriteType(Type? type) => type is not null;

    public override async Task WriteResponseBodyAsync(OutputFormatterWriteContext context)
    {
        if (context.Object is null)
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var response = context.HttpContext.Response;
        response.ContentType = MediaType;

        // Serialise into a pooled MemoryStream so we can set Content-Length
        // before copying to the response body — required by some HTTP/1.1 proxies
        // and beneficial for HTTP/2 frame sizing.
        await using var buffer = new MemoryStream();
        RuntimeTypeModel.Default.Serialize(buffer, context.Object);
        response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(response.Body, context.HttpContext.RequestAborted);
    }
}
