﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Vostok.Applications.AspNetCore.Configuration;
using Vostok.Applications.AspNetCore.Models;
using Vostok.Clusterclient.Core.Model;
using Vostok.Commons.Formatting;
using Vostok.Commons.Time;
using Vostok.Context;
using Vostok.Logging.Abstractions;

namespace Vostok.Applications.AspNetCore.Middlewares
{
    /// <summary>
    /// Logs incoming requests and outgoing responses.
    /// </summary>
    [PublicAPI]
    public class LoggingMiddleware
    {
        private const int StringBuilderCapacity = 256;

        private readonly RequestDelegate next;
        private readonly IOptions<LoggingSettings> options;
        private readonly ILog log;

        public LoggingMiddleware(
            [NotNull] RequestDelegate next,
            [NotNull] IOptions<LoggingSettings> options,
            [NotNull] ILog log)
        {
            this.next = next ?? throw new ArgumentNullException(nameof(next));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            LogRequest(context.Request);

            var watch = Stopwatch.StartNew();

            await next(context);

            LogResponse(context.Request, context.Response, watch.Elapsed);
        }

        private static void AppendSegment(StringBuilder builder, object[] parameters, string templateSegment, object parameter, ref int parameterIndex)
        {
            builder.Append(templateSegment);

            parameters[parameterIndex++] = parameter;
        }

        private static string GetClientConnectionInfo(HttpRequest request)
        {
            var connection = request.HttpContext.Connection;
            return $"{connection.RemoteIpAddress}:{connection.RemotePort}";
        }

        private static string FormatPath(StringBuilder builder, HttpRequest request, LoggingCollectionSettings querySettings)
        {
            return FormatAndRollback(
                builder,
                b =>
                {
                    b.Append(request.Method);
                    b.Append(" ");
                    b.Append(request.Path);

                    if (querySettings.IsEnabledForRequest(request))
                    {
                        if (querySettings.IsEnabledForAllKeys())
                        {
                            b.Append(request.QueryString);
                        }
                        else
                        {
                            var writtenFirst = false;

                            foreach (var pair in request.Query.Where(kvp => querySettings.IsEnabledForKey(kvp.Key)))
                            {
                                if (!writtenFirst)
                                {
                                    b.Append('?');
                                    writtenFirst = true;
                                }

                                b.Append($"{pair.Key}={pair.Value}");
                            }
                        }
                    }
                });
        }

        private static string FormatHeaders(StringBuilder builder, IHeaderDictionary headers, LoggingCollectionSettings settings)
        {
            return FormatAndRollback(
                builder,
                b =>
                {
                    foreach (var (key, value) in headers)
                    {
                        if (!settings.IsEnabledForKey(key))
                            continue;

                        b.AppendLine();
                        b.Append('\t');
                        b.Append(key);
                        b.Append(": ");
                        b.Append(value);
                    }
                });
        }

        private static string FormatAndRollback(StringBuilder builder, Action<StringBuilder> format)
        {
            var positionBefore = builder.Length;

            format(builder);

            var result = builder.ToString(positionBefore, builder.Length - positionBefore);

            builder.Length = positionBefore;

            return result;
        }

        private void LogRequest(HttpRequest request)
        {
            var requestInfo = FlowingContext.Globals.Get<IRequestInfo>();
            var builder = StringBuilderCache.Acquire(StringBuilderCapacity);

            var addClientIdentity = requestInfo.ClientApplicationIdentity != null;
            var addBodySize = request.ContentLength > 0L;
            var addHeaders = options.Value.LogRequestHeaders.IsEnabledForRequest(request);

            var parametersCount = 3 + (addClientIdentity ? 1 : 0) + (addBodySize ? 1 : 0) + (addHeaders ? 1 : 0);
            var parameters = new object[parametersCount];
            var parametersIndex = 0;

            AppendSegment(builder, parameters, "Received request '{Request}' from", FormatPath(builder, request, options.Value.LogQueryString), ref parametersIndex);

            if (addClientIdentity)
                AppendSegment(builder, parameters, " '{ClientIdentity}' at", requestInfo.ClientApplicationIdentity, ref parametersIndex);

            AppendSegment(builder, parameters, " '{RequestConnection}'", GetClientConnectionInfo(request), ref parametersIndex);
            AppendSegment(builder, parameters, " with timeout = {Timeout}", requestInfo.Timeout.ToPrettyString(), ref parametersIndex);

            builder.Append('.');

            if (addBodySize)
                AppendSegment(builder, parameters, " Body size = {BodySize}.", request.ContentLength, ref parametersIndex);

            if (addHeaders)
                AppendSegment(builder, parameters, " Request headers: {RequestHeaders}", FormatHeaders(builder, request.Headers, options.Value.LogRequestHeaders), ref parametersIndex);

            log.Info(builder.ToString(), parameters);

            StringBuilderCache.Release(builder);
        }

        private void LogResponse(HttpRequest request, HttpResponse response, TimeSpan elapsed)
        {
            var builder = StringBuilderCache.Acquire(StringBuilderCapacity);

            var addBodySize = response.ContentLength > 0;
            var addHeaders = options.Value.LogResponseHeaders.IsEnabledForRequest(request);

            builder.Append("Response code = {ResponseCode:D} ('{ResponseCode}'). Time = {ElapsedTime}.");

            if (addBodySize)
                builder.Append(" Body size = {BodySize}.");

            if (addHeaders)
                builder.Append(" Response headers: {ResponseHeaders}");

            var logEvent = new LogEvent(LogLevel.Info, PreciseDateTime.Now, builder.ToString())
                .WithProperty("ResponseCode", (ResponseCode)response.StatusCode)
                .WithProperty("ElapsedTime", elapsed.ToPrettyString())
                .WithProperty("ElapsedTimeMs", elapsed.TotalMilliseconds);

            if (addBodySize)
                logEvent = logEvent.WithProperty("BodySize", response.ContentLength);

            if (addHeaders)
                logEvent = logEvent.WithProperty("ResponseHeaders", FormatHeaders(builder, response.Headers, options.Value.LogResponseHeaders));

            log.Log(logEvent);

            StringBuilderCache.Release(builder);
        }
    }
}