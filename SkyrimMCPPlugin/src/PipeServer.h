#pragma once

#include <atomic>
#include <cstdint>
#include <list>
#include <mutex>
#include <thread>

// Forward declare HANDLE as void* to avoid including windows.h in header
// (CommonLibSSE-NG requires its headers before any Windows API includes)
using PipeHandle = void*;

namespace SkyrimMCP {

    class PipeServer {
    public:
        PipeServer();
        ~PipeServer();

        void Start();
        void Stop();

    private:
        void ServerLoop();
        void HandleClient(PipeHandle pipe);
        void ReapFinishedClients();

        // INFRA-023: each connected client is served on its own thread (Claude Desktop's server plus, say, a dev tool or a
        // fork's in-game chat). Requests still take turns inside HostCore.
        struct Client {
            std::thread thread;
            PipeHandle pipe = nullptr;
            std::atomic<bool> finished{ false };
        };
        std::mutex _clientsMutex;
        std::list<Client> _clients;
        static constexpr std::size_t MAX_CLIENTS = 4;

        std::thread _thread;
        std::atomic<bool> _running{false};
        PipeHandle _stopEvent;

        static constexpr const char* PIPE_NAME = "\\\\.\\pipe\\SkyrimMCP";
        static constexpr unsigned long BUFFER_SIZE = 65536;
    };

}
