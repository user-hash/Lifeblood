#ifndef DIRECT_REFS_PACKET_H
#define DIRECT_REFS_PACKET_H

enum PacketKind {
    PacketKind_Audio = 1,
    PacketKind_Video = 2
};

typedef enum PacketKind PacketKindAlias;

struct Packet {
    PacketKindAlias kind;
    int size;
};

extern int decode_bias;

#endif
