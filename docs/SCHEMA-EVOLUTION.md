# Schema evolution with Protocol Buffers

`ProtoSerializer<T>` writes real generated `Google.Protobuf.IMessage<T>` messages. The source generator's tagged wire remains a separate format; a similar field layout does not make the two formats interchangeable.

Run the complete example:

```sh
dotnet run --project src/Samples/Boutquin.Storage.Samples.csproj -- protobuf
```

Three checked-in `.proto` definitions and generated C# message classes demonstrate:

1. An original name and score.
2. A name rename with the same tag, an added email field, and a deprecated score.
3. Removal of score with both its number and name reserved permanently.

The latest reader replays all three versions. The old reader also changes its known name field and writes the message again: the newer email survives in its unknown fields. Reconstructing a fresh message from known properties would lose that information; preserve the parsed message when forwarding data.

## Framing and registry contract

A record consists of eight header bytes followed by the standard Protobuf body:

| Bytes | Meaning |
|---|---|
| 0–3 | Positive registry version, little-endian signed int32 |
| 4–7 | Payload length, little-endian signed int32 |
| 8 onward | Exactly that many standard Protobuf bytes |

This Storage envelope makes concatenated records unambiguous. It is not Protobuf's varint-delimited framing or Confluent's wire format. External consumers can parse the body using their generated parser; translate the envelope at an integration boundary.

The caller selects the subject and matching generated parser. Deserialization validates that the writer's version exists under that subject; it does not generate a reader from registry metadata. Unknown fields are retained even if the supplied parser requested discarding them. The default payload limit is 16 MiB, configurable at construction. Oversized lengths fail before payload allocation, and nonseekable streams grow buffers only from observed bytes. Malformed/truncated frames and unregistered versions raise `UnsupportedFormatVersionException`; cancellation propagates. A failed stream write can leave a partial frame, so use the event log or another atomic durable container when records must survive crashes.

The sample registers explicit tag shapes through the existing `TaggedWireCompatibilityChecker`, validating each shape against its generated external Protobuf descriptor with `ProtobufTagCompatibilityChecker`. It uses `sint32` for the score to match the signed zigzag tag shape. This bridge covers these scalar fields; it is not a universal compatibility checker for arbitrary Protobuf messages, oneofs, maps or recursive types. Every registered version is checked against all earlier versions. The tests reject type changes at an existing tag and reuse of a retired tag.

`InMemorySchemaRegistry` is volatile. Persist or reconstruct the same subject/version assignments before replay; assigning a different schema to an old version makes the envelope meaningless. Deprecation is a source-level signal, while reserved numbers prevent later field reuse. Both binary readers and the registry need stable field identity.

## Trade-off

Generated Protobuf gives external tooling and readers a shared wire contract. The envelope adds registry identity and bounded framing but requires explicit subject and parser coordination. The native tagged wire retains Storage's generated value/key behavior. Choose the encoding at the boundary where interoperability is needed.

The generated fixtures were produced by `protoc` 36.1, matching the pinned runtime family. To regenerate them:

```sh
protoc --proto_path=src/Samples/Proto --csharp_out=src/Samples/Proto src/Samples/Proto/evolution_v1.proto src/Samples/Proto/evolution_v2.proto src/Samples/Proto/evolution_v3.proto
```

See the official [C# generated-code guide](https://protobuf.dev/reference/csharp/csharp-generated/) and [Protocol Buffers language guide](https://protobuf.dev/programming-guides/proto3/) for generated messages, unknown fields and reserved field numbers.
