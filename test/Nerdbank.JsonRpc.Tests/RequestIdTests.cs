// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class RequestIdTests
{
	[Test]
	public void Equals_Object()
	{
		Assert.True(default(RequestId).Equals((object?)default(RequestId)));
		Assert.False(default(RequestId).Equals((object?)new RequestId(3)));
		Assert.True(new RequestId(3).Equals((object?)new RequestId(3)));
		Assert.False(default(RequestId).Equals((object?)null));
		Assert.False(new RequestId("a"u8.ToArray()).Equals((object?)"a"));
	}

	[Test]
	public void MemoryOfByteConverter()
	{
		RequestId id = (ReadOnlyMemory<byte>)"abc"u8.ToArray();
		Assert.Equal("abc", id.ToString());
	}

	[Test]
	public void StringConstructor()
	{
		RequestId id = new("abc");
		Assert.Equal("abc", id.ToString());
		Assert.Equal((RequestId)"abc", id);
	}

	[Test]
	public void Default_Value()
	{
		Assert.Equal(default(RequestId), default(RequestId));
		Assert.Equal(default(RequestId).GetHashCode(), default(RequestId).GetHashCode());
		Assert.Equal("null", default(RequestId).ToString());
		AssertRoundTrip(default);
	}

	[Test]
	public void Long_Value()
	{
		RequestId id1 = 1, id2 = 2;
		Assert.NotEqual(id1, id2);
		Assert.NotEqual(id1.GetHashCode(), id2.GetHashCode());
		Assert.Equal("1", id1.ToString());
		AssertRoundTrip(id1);
	}

	[Test]
	public void UInt64_MaxValueRoundTrips()
	{
		RequestId maximum = new(ulong.MaxValue);
		Assert.Equal(ulong.MaxValue.ToString(), maximum.ToString());
		Assert.NotEqual(new RequestId(long.MaxValue), maximum);
		AssertRoundTrip(maximum);
	}

	[Test]
	public void String_Value()
	{
		RequestId id1 = "a", id2 = "b";
		Assert.NotEqual(id1, id2);
		Assert.NotEqual(id1.GetHashCode(), id2.GetHashCode());
		Assert.Equal("a", id1.ToString());

		AssertRoundTrip(id1);

		byte[] stringBuffer1 = new byte[3], stringBuffer2 = new byte[3];
		"abc"u8.CopyTo(stringBuffer1);
		stringBuffer1.CopyTo(stringBuffer2);
		id1 = new(stringBuffer1);
		id2 = new(stringBuffer2);
		Assert.Equal(id1, id2);
		Assert.Equal("abc", id1.ToString());

		stringBuffer2 = new byte[3];
		"def"u8.CopyTo(stringBuffer2);
		id2 = new(stringBuffer2);
		Assert.NotEqual(id1, id2);
	}

	[Test]
	public void DefaultAndEmptyStringAreDistinct()
	{
		RequestId empty = string.Empty;

		Assert.NotEqual(default, empty);
		Assert.NotEqual(default(RequestId).GetHashCode(), empty.GetHashCode());
	}

	private static void AssertRoundTrip(RequestId requestId) => Assert.Equal(requestId, Roundtrip(requestId));

	private static RequestId Roundtrip(RequestId requestId) => JsonRpcMessagePackChannel.DefaultSerializer.Deserialize<RequestId>(JsonRpcMessagePackChannel.DefaultSerializer.Serialize(requestId));
}
