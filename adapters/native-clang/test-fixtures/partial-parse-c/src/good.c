#include "good.h"

static int normalize(int value)
{
    return value < 0 ? 0 : value;
}

int decode_good(Packet* packet)
{
    return normalize(packet->size);
}
