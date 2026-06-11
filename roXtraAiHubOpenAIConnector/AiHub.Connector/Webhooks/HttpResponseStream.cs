using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiHub.Connector.Webhooks;

internal sealed class HttpResponseStream : Stream, IAsyncDisposable
{
	private readonly HttpResponseMessage _response;
	private readonly Stream _inner;
	private long _readBytes;
	private bool _disposed;

	public HttpResponseStream(HttpResponseMessage response, Stream inner)
	{
		_response = response ?? throw new ArgumentNullException(nameof(response));
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
	}

	public override bool CanRead => _inner.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position
	{
		get => _readBytes;
		set => throw new NotSupportedException();
	}

	public override void Flush() => _inner.Flush();

	public override int Read(byte[] buffer, int offset, int count)
	{
		var read = _inner.Read(buffer, offset, count);
		UpdateRead(read);
		return read;
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
		UpdateRead(read);
		return read;
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		return CoreAsync(buffer, cancellationToken);
	}

	private async ValueTask<int> CoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
	{
		var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
		UpdateRead(read);
		return read;
	}

	private void UpdateRead(int read)
	{
		if (read <= 0)
			return;
		_readBytes += read;
	}

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (_disposed)
			return;
		_disposed = true;
		if (disposing)
		{
			_inner.Dispose();
			_response.Dispose();
		}
		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		_disposed = true;
		try
		{
			await _inner.DisposeAsync().ConfigureAwait(false);
		}
		finally
		{
			_response.Dispose();
		}
		GC.SuppressFinalize(this);
	}
}
