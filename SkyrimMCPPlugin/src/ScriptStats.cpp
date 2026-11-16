#include "ScriptStats.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace SkyrimMCP::ScriptStats {

    namespace {
        using Stack = RE::BSScript::Stack;

        const char* StateName(Stack::State state) {
            switch (state) {
                case Stack::State::kRunning: return "running";
                case Stack::State::kFinished: return "finished";
                case Stack::State::kWaitingOnMemory: return "waitingOnMemory";
                case Stack::State::kWaitingOnLatentFunction: return "waitingOnLatentFunction";
                case Stack::State::kWaitingOnOtherStackForCall: return "waitingOnOtherStackForCall";
                case Stack::State::kWaitingOnOtherStackForReturn: return "waitingOnOtherStackForReturn";
                case Stack::State::kWaitingOnOtherStackForReturnNoPop: return "waitingOnOtherStackForReturnNoPop";
                case Stack::State::kRetryReturnNoPop: return "retryReturnNoPop";
                case Stack::State::kRetryCall: return "retryCall";
                default: return "unknown";
            }
        }

        std::string FrameName(const RE::BSScript::StackFrame* frame) {
            if (!frame) return "(no frame)";
            auto* fn = frame->owningFunction.get();
            if (!fn) return "(unknown function)";
            const char* type = fn->GetObjectTypeName().c_str();
            const char* name = fn->GetName().c_str();
            return std::string(type && type[0] ? type : "?") + "." + (name && name[0] ? name : "?");
        }

        std::string ScriptOf(const RE::BSScript::StackFrame* frame) {
            auto* fn = frame ? frame->owningFunction.get() : nullptr;
            const char* type = fn ? fn->GetObjectTypeName().c_str() : nullptr;
            return (type && type[0]) ? type : "?";
        }

        json TopN(const std::map<std::string, int>& counts, int top) {
            std::vector<std::pair<std::string, int>> sorted(counts.begin(), counts.end());
            std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) {
                return a.second != b.second ? a.second > b.second : a.first < b.first;
            });
            json out = json::array();
            for (int i = 0; i < static_cast<int>(sorted.size()) && i < top; ++i) {
                out.push_back({{"name", sorted[i].first}, {"stacks", sorted[i].second}});
            }
            return out;
        }
    }

    json GetScriptStats(int top) {
        top = std::clamp(top, 1, 100);
        auto* vm = RE::SkyrimVM::GetSingleton();
        // The engine's only IVirtualMachine is Internal::VirtualMachine (its first base).
        auto* impl = vm ? static_cast<RE::BSScript::Internal::VirtualMachine*>(vm->GetVMRuntimeData().impl.get()) : nullptr;
        if (!impl) return {{"error", "The Papyrus VM is not available yet"}};

        json result;
        result["overstressed"] = impl->overstressed;
        result["waitingFunctionMessages"] = impl->uiWaitingFunctionMessages;
        // scriptCount/arrayCount are only filled in while saving (they read 0 in play), so count
        // the live tables instead.
        {
            RE::BSSpinLockGuard guard(impl->attachedScriptsLock);
            result["objectsWithScripts"] = impl->attachedScripts.size();
        }
        {
            RE::BSSpinLockGuard guard(impl->arraysLock);
            result["arrays"] = impl->arrays.size();
        }
        result["frozenStacks"] = impl->frozenStacksCount;

        std::map<std::string, int> byState, byCurrentFunction, byScript, byEntryPoint;
        std::size_t running = 0, latent = 0;
        {
            RE::BSSpinLockGuard guard(impl->runningStacksLock);
            running = impl->allRunningStacks.size();
            latent = impl->waitingLatentReturns.size();
            // A count this large means the VM layout doesn't match this game version: don't walk it.
            if (running > 200000) {
                return {{"error", "Papyrus VM data doesn't look valid for this game version (stack count " + std::to_string(running) + ")"}};
            }
            for (const auto& entry : impl->allRunningStacks) {
                const auto* stack = entry.second.get();
                if (!stack) continue;
                ++byState[StateName(stack->state.get())];
                const auto* topFrame = stack->top;
                if (!topFrame) continue;
                ++byCurrentFunction[FrameName(topFrame)];
                // The bottom frame is where the call started: usually an event in the mod's own script.
                const auto* bottom = topFrame;
                std::set<std::string> scripts;
                for (int depth = 0; bottom && depth < 256; ++depth) {
                    scripts.insert(ScriptOf(bottom));
                    if (!bottom->previousFrame) break;
                    bottom = bottom->previousFrame;
                }
                ++byEntryPoint[FrameName(bottom)];
                for (const auto& s : scripts) ++byScript[s];
            }
        }

        result["runningStacks"] = running;
        result["waitingLatentReturns"] = latent;
        result["stacksByState"] = byState;
        result["topScripts"] = TopN(byScript, top);
        result["topEntryPoints"] = TopN(byEntryPoint, top);
        result["topCurrentFunctions"] = TopN(byCurrentFunction, top);
        result["note"] =
            "A snapshot. Stacks waiting on a latent function (Utility.Wait, RegisterForSingleUpdate loops) are normal in small numbers; "
            "hundreds of stacks from one script, a high waitingFunctionMessages count or overstressed=true point to that mod as the cause of script lag. "
            "topEntryPoints is where each call started (usually the mod's own event handler).";
        return result;
    }

}
