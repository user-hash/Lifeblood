#include "packet.h"

int decode_bias = 3;

static int clamp(int value)
{
    return value < 0 ? 0 : value;
}

int decode(struct Packet *packet)
{
    int adjusted = packet->size + decode_bias;
    if (packet->kind == PacketKind_Video) {
        adjusted = adjusted + decode_bias;
    }

    return clamp(adjusted);
}
