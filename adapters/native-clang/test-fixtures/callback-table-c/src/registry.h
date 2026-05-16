#ifndef CALLBACK_TABLE_REGISTRY_H
#define CALLBACK_TABLE_REGISTRY_H

struct Packet {
    int size;
};

typedef int (*PacketHandler)(struct Packet *packet);

struct CodecRegistration {
    const char *name;
    PacketHandler handler;
};

extern struct CodecRegistration codec_table[];

#endif
