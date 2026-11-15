// CommonLibSSE headers MUST come before Windows API
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

// Now safe to include Windows API
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "PipeServer.h"
#include "HostCore.h"

#include <string>
#include <vector>

namespace SkyrimMCP {

    PipeServer::PipeServer() {
        _stopEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    }

    PipeServer::~PipeServer() {
        Stop();
        if (_stopEvent != INVALID_HANDLE_VALUE) {
            CloseHandle(static_cast<HANDLE>(_stopEvent));
        }
    }

    void PipeServer::Start() {
        if (_running.exchange(true)) return;

        ResetEvent(static_cast<HANDLE>(_stopEvent));
        _thread = std::thread(&PipeServer::ServerLoop, this);
    }

    void PipeServer::Stop() {
        if (!_running.exchange(false)) return;

        SetEvent(static_cast<HANDLE>(_stopEvent));

        HANDLE dummy = CreateFileA(
            PIPE_NAME, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, 0, nullptr);
        if (dummy != INVALID_HANDLE_VALUE) {
            CloseHandle(dummy);
        }

        if (_thread.joinable()) {
            _thread.join();
        }
    }

    void PipeServer::ServerLoop() {
        SKSE::log::info("Pipe server thread started");

        while (_running) {
            HANDLE pipe = CreateNamedPipeA(
                PIPE_NAME,
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                BUFFER_SIZE,
                BUFFER_SIZE,
                0,
                nullptr);

            if (pipe == INVALID_HANDLE_VALUE) {
                SKSE::log::error("CreateNamedPipe failed: {}", GetLastError());
                Sleep(1000);
                continue;
            }

            SKSE::log::info("Waiting for pipe client connection...");

            BOOL connected = ConnectNamedPipe(pipe, nullptr)
                ? TRUE
                : (GetLastError() == ERROR_PIPE_CONNECTED ? TRUE : FALSE);

            if (!_running) {
                CloseHandle(pipe);
                break;
            }

            if (connected) {
                SKSE::log::info("Client connected to pipe");
                HandleClient(static_cast<PipeHandle>(pipe));
                SKSE::log::info("Client disconnected");
            }

            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }

        SKSE::log::info("Pipe server thread stopped");
    }

    void PipeServer::HandleClient(PipeHandle pipeHandle) {
        HANDLE pipe = static_cast<HANDLE>(pipeHandle);
        std::vector<char> buffer(BUFFER_SIZE);
        std::string lineBuffer;

        while (_running) {
            DWORD bytesRead = 0;
            BOOL success = ReadFile(pipe, buffer.data(), BUFFER_SIZE - 1, &bytesRead, nullptr);

            if (!success || bytesRead == 0) {
                DWORD err = GetLastError();
                if (err == ERROR_BROKEN_PIPE) {
                    break;
                }
                SKSE::log::error("ReadFile failed: {}", err);
                break;
            }

            lineBuffer.append(buffer.data(), bytesRead);

            size_t pos;
            while ((pos = lineBuffer.find('\n')) != std::string::npos) {
                std::string line = lineBuffer.substr(0, pos);
                lineBuffer.erase(0, pos + 1);

                if (!line.empty() && line.back() == '\r') {
                    line.pop_back();
                }

                if (line.empty()) continue;

                SKSE::log::info("Received request: {}", line);

                std::string response = HostCore::HandleRequest(line);

                SKSE::log::info("Sending response: {}", response);

                DWORD bytesWritten = 0;
                success = WriteFile(pipe, response.c_str(),
                    static_cast<DWORD>(response.size()), &bytesWritten, nullptr);

                if (!success) {
                    SKSE::log::error("WriteFile failed: {}", GetLastError());
                    return;
                }

                FlushFileBuffers(pipe);
            }
        }
    }

}
