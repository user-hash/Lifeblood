#include "packet.h"

static int clamp(int value)
{
    return value < 0 ? 0 : value;
}

int decode(struct Packet *packet)
{
    return clamp(packet->size);
}
