struct Packet {
    int size;
};

struct Packet *current_packet(void)
{
    return 0;
}

struct Packet *echo_packet(struct Packet *packet)
{
    return packet;
}
