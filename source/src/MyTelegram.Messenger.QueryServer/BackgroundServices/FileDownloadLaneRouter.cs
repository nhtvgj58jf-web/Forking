using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Upload;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

/// <summary>
/// Keeps file/download-lane RPCs away from the closed-source file-server binary when
/// they actually belong to messenger. Some session-server paths publish directly to
/// the file-server queue, so query-server owns the legacy ingress and forwards only
/// real file operations to a private file-server queue.
/// </summary>
public sealed class FileDownloadLaneRouter(
    ILogger<FileDownloadLaneRouter> logger,
    IOptionsMonitor<RabbitMqOptions> rabbitMqOptions,
    IRabbitMqSerializer rabbitMqSerializer,
    MessengerQueryDataProcessor queryDataProcessor,
    IFileReferenceHelper fileReferenceHelper,
    IUserAccessHashKeyCache userAccessHashKeyCache)
    : BackgroundService, IFileDownloadLaneRouter, IDisposable
{
    private const string SourceExchange = "mytelegram_exchange";
    private const string FileDownloadIngressExchange = "mytelegram_file_download_exchange";
    private const string FileServerExchange = "mytelegram_file_server_exchange";
    private const string QueryServerQueue = "MyTelegramMessengerQueryServer";
    private const string DirectFileServerQueue = "MyTelegramFileServer";
    private const string FileServerQueue = "MyTelegramFileServerRaw";
    private const uint InputGroupCallStreamConstructorId = 0x0598a92a;
    private static readonly string[] RoutedKeys =
    [
        nameof(DownloadDataReceivedEvent),
        nameof(UploadDataReceivedEvent)
    ];

    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private IChannel? _directConsumerChannel;

    public async Task ForwardAsync(DownloadDataReceivedEvent eventData)
    {
        await ForwardAsync(eventData, nameof(DownloadDataReceivedEvent));
    }

    public async Task ForwardAsync(UploadDataReceivedEvent eventData)
    {
        await ForwardAsync(eventData, nameof(UploadDataReceivedEvent));
    }

    private async Task ForwardAsync(DataReceivedEvent eventData, string routingKey)
    {
        using var writer = new CommunityToolkit.HighPerformance.Buffers.ArrayPoolBufferWriter<byte>();
        rabbitMqSerializer.Serialize(writer, eventData);
        await ForwardRawAsync(writer.WrittenMemory, routingKey);
    }

    private async Task ForwardRawAsync(ReadOnlyMemory<byte> body, string routingKey)
    {
        await _publishLock.WaitAsync();
        try
        {
            await EnsureTopologyCoreAsync(CancellationToken.None);
            await _channel!.BasicPublishAsync(
                exchange: FileServerExchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent },
                body: body);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    public override void Dispose()
    {
        _directConsumerChannel?.Dispose();
        _channel?.Dispose();
        _connection?.Dispose();
        _publishLock.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureTopologyAsync(stoppingToken);
                await EnsureDirectFileServerConsumerAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to maintain filtered file download lane");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task EnsureDirectFileServerConsumerAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || !_connection.IsOpen)
        {
            return;
        }

        if (_directConsumerChannel is { IsOpen: true })
        {
            return;
        }

        _directConsumerChannel?.Dispose();
        _directConsumerChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        _directConsumerChannel.CallbackExceptionAsync += (_, ea) =>
        {
            logger.LogWarning(ea.Exception, "Error with direct file-server queue consumer channel");
            return Task.CompletedTask;
        };

        await _directConsumerChannel.QueueDeclareAsync(
            queue: DirectFileServerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        await _directConsumerChannel.BasicQosAsync(0, 32, false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_directConsumerChannel);
        consumer.ReceivedAsync += OnDirectFileServerMessageReceived;

        await _directConsumerChannel.BasicConsumeAsync(
            queue: DirectFileServerQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        logger.LogInformation("Direct file-server queue interceptor started on {Queue}", DirectFileServerQueue);
    }

    private async Task OnDirectFileServerMessageReceived(object sender, BasicDeliverEventArgs eventArgs)
    {
        try
        {
            await ProcessDirectFileServerMessageAsync(eventArgs.RoutingKey, eventArgs.Body);
            await _directConsumerChannel!.BasicAckAsync(eventArgs.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process direct file-server queue message, routingKey: {RoutingKey}", eventArgs.RoutingKey);
            await _directConsumerChannel!.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true);
        }
    }

    private async Task ProcessDirectFileServerMessageAsync(string routingKey, ReadOnlyMemory<byte> body)
    {
        switch (routingKey)
        {
            case nameof(UploadDataReceivedEvent):
            {
                var eventData = rabbitMqSerializer.Deserialize<UploadDataReceivedEvent>(body);
                if (await TryRerouteGroupCallStreamFileAsync(eventData))
                {
                    return;
                }

                await ForwardRawAsync(body, nameof(UploadDataReceivedEvent));
                return;
            }
            case nameof(DownloadDataReceivedEvent):
            {
                var eventData = rabbitMqSerializer.Deserialize<DownloadDataReceivedEvent>(body);
                if (await TryRerouteGroupCallStreamFileAsync(eventData))
                {
                    return;
                }

                await ForwardRawAsync(body, nameof(DownloadDataReceivedEvent));
                return;
            }
        }

        var uploadEvent = TryDeserialize<UploadDataReceivedEvent>(body);
        if (uploadEvent is not null && await TryRerouteGroupCallStreamFileAsync(uploadEvent))
        {
            return;
        }

        if (uploadEvent is not null)
        {
            await ForwardRawAsync(body, nameof(UploadDataReceivedEvent));
            return;
        }

        var downloadEvent = TryDeserialize<DownloadDataReceivedEvent>(body);
        if (downloadEvent is not null && await TryRerouteGroupCallStreamFileAsync(downloadEvent))
        {
            return;
        }

        if (downloadEvent is not null)
        {
            await ForwardRawAsync(body, nameof(DownloadDataReceivedEvent));
            return;
        }

        logger.LogWarning(
            "Forwarding unknown direct file-server queue message without reroute, routingKey: {RoutingKey}, bodyLength: {BodyLength}",
            routingKey,
            body.Length);
        await ForwardRawAsync(body, routingKey);
    }

    private TEventData? TryDeserialize<TEventData>(ReadOnlyMemory<byte> body)
        where TEventData : class
    {
        try
        {
            return rabbitMqSerializer.Deserialize<TEventData>(body);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to deserialize direct file-server queue message as {Type}", typeof(TEventData).Name);
            return null;
        }
    }

    private async Task<bool> TryRerouteGroupCallStreamFileAsync(DataReceivedEvent eventData)
    {
        if (eventData.ObjectId == 0xd9ab0f54)
        {
            await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
            logger.LogInformation("Rerouting messages.getCustomEmojiDocuments from direct file-server queue to messenger query lane, reqMsgId: {ReqMsgId}", eventData.ReqMsgId);
            await queryDataProcessor.ProcessAsync(
                new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                    eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                    eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId));
            return true;
        }

        if (!IsGetFileObjectId(eventData.ObjectId))
        {
            return false;
        }

        var reason = GetRerouteReason(eventData);
        if (reason == null)
        {
            return false;
        }

        await userAccessHashKeyCache.RememberAsync(eventData.UserId, eventData.AccessHashKeyId);
        logger.LogInformation(
            "Rerouting upload.getFile from direct file-server queue to messenger query lane ({Reason}), reqMsgId: {ReqMsgId}",
            reason,
            eventData.ReqMsgId);
        await queryDataProcessor.ProcessAsync(
            new MessengerQueryDataReceivedEvent(eventData.ConnectionId, eventData.ConnectionType, eventData.RequestId, eventData.ObjectId,
                eventData.UserId, eventData.ReqMsgId, eventData.SeqNumber, eventData.AuthKeyId, eventData.PermAuthKeyId,
                eventData.Data, eventData.Layer, eventData.Date, eventData.DeviceType, eventData.ClientIp, eventData.SessionId, eventData.AccessHashKeyId));
        return true;
    }

    /// <summary>
    /// Why this <c>upload.getFile</c> must be answered by the messenger instead of the file-server, or
    /// <c>null</c> to forward it as usual.
    ///
    /// <para>Two reasons. A group-call stream is served from here because the file-server knows nothing
    /// about it. And a request whose <c>file_reference</c> this server did not issue has to be refused with
    /// <c>FILE_REFERENCE_*</c>, which the file-server would never do — it ignores the field — leaving the
    /// client with media it can neither download nor repair. Only the failures are diverted: a valid
    /// request still goes straight to the file-server, because this repository cannot serve stored
    /// documents at all. See https://corefork.telegram.org/api/file-references</para>
    /// </summary>
    private string? GetRerouteReason(DataReceivedEvent eventData)
    {
        RequestGetFile? request = null;
        try
        {
            request = eventData.Data.ToTObject<IObject>() as RequestGetFile;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Failed to parse upload.getFile while checking direct file-server queue reroute, reqMsgId: {ReqMsgId}",
                eventData.ReqMsgId);
        }

        if (request == null)
        {
            return ContainsConstructorId(eventData.Data.Span, InputGroupCallStreamConstructorId)
                ? "inputGroupCallStream"
                : null;
        }

        if (request.Location is TInputGroupCallStream)
        {
            return "inputGroupCallStream";
        }

        var state = request.Location switch
        {
            TInputDocumentFileLocation document => fileReferenceHelper.Validate(document.FileReference.Span,
                AccessHashType.Document, document.Id),
            TInputPhotoFileLocation photo => fileReferenceHelper.Validate(photo.FileReference.Span,
                AccessHashType.Photo, photo.Id),
            _ => FileReferenceState.Valid
        };

        if (state == FileReferenceState.Valid)
        {
            return null;
        }

        if (fileReferenceHelper.Mode != FileReferenceMode.Enforce)
        {
            logger.LogWarning(
                "upload.getFile carries a file_reference that would have been refused: state={State}, reqMsgId={ReqMsgId}. Forwarded to the file-server because file references are not enforced.",
                state,
                eventData.ReqMsgId);
            return null;
        }

        return $"file_reference {state}";
    }

    private static bool IsGetFileObjectId(uint objectId)
    {
        return objectId is ObjectIdConsts.GetFileObjectId or ObjectIdConsts.GetFileObjectIdLayer143;
    }

    private static bool ContainsConstructorId(ReadOnlySpan<byte> data, uint constructorId)
    {
        var bytes = BitConverter.GetBytes(constructorId);
        for (var i = 0; i <= data.Length - bytes.Length; i++)
        {
            if (data.Slice(i, bytes.Length).SequenceEqual(bytes))
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureTopologyCoreAsync(cancellationToken);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task EnsureTopologyCoreAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || !_connection.IsOpen)
        {
            var options = rabbitMqOptions.CurrentValue;
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                AutomaticRecoveryEnabled = true
            };

            _connection?.Dispose();
            _connection = await factory.CreateConnectionAsync(cancellationToken);
        }

        if (_channel is null || !_channel.IsOpen)
        {
            _channel?.Dispose();
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        }

        await _channel.ExchangeDeclareAsync(
            exchange: SourceExchange,
            type: "direct",
            cancellationToken: cancellationToken);
        await _channel.ExchangeDeclareAsync(
            exchange: FileDownloadIngressExchange,
            type: "direct",
            durable: true,
            cancellationToken: cancellationToken);
        await _channel.ExchangeDeclareAsync(
            exchange: FileServerExchange,
            type: "direct",
            durable: true,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: QueryServerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: DirectFileServerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        await _channel.QueueDeclareAsync(
            queue: FileServerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        foreach (var routingKey in RoutedKeys)
        {
            await _channel.QueueBindAsync(
                queue: QueryServerQueue,
                exchange: FileDownloadIngressExchange,
                routingKey: routingKey,
                cancellationToken: cancellationToken);
            await _channel.QueueBindAsync(
                queue: FileServerQueue,
                exchange: FileServerExchange,
                routingKey: routingKey,
                cancellationToken: cancellationToken);
            await _channel.QueueUnbindAsync(
                queue: DirectFileServerQueue,
                exchange: FileServerExchange,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: cancellationToken);
            await _channel.QueueUnbindAsync(
                queue: FileServerQueue,
                exchange: FileDownloadIngressExchange,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: cancellationToken);
            await _channel.QueueUnbindAsync(
                queue: FileServerQueue,
                exchange: SourceExchange,
                routingKey: routingKey,
                arguments: null,
                cancellationToken: cancellationToken);
        }
    }
}
