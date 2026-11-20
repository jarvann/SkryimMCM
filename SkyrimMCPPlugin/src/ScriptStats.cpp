#include "ScriptStats.h"

#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>

#include <algorithm>
#include <cctype>
#include <map>
#include <set>
#include <string>
#include <unordered_map>
#include <format>
#include <vector>

// windows.h (pulled in through SKSE) defines GetObject as GetObjectA, which hides Variable::GetObject.
#ifdef GetObject
#undef GetObject
#endif

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

        // FEAT-268: the plugin that defines the object a frame runs on (its "self"), and the last plugin that changed it when
        // that differs ("Fading of Followers.esp, last changed by Ducki_Follower.esp"): the overriding mod is often the
        // one behind a problem. GetFile(0) is the defining plugin, GetFile(-1) the last override.
        std::string Describe(const RE::TESForm* form) {
            auto* defined = form->GetFile(0);
            auto* last = form->GetFile(-1);
            if (!defined) return {};
            std::string text(defined->GetFilename());
            if (last && last != defined) text += ", last changed by " + std::string(last->GetFilename());
            return text;
        }

        std::string OwnerOf(const RE::BSScript::Variable& self);

        std::string OwnerPlugin(const RE::BSScript::StackFrame* frame) {
            return frame ? OwnerOf(frame->self) : std::string{};
        }

        std::string OwnerOf(const RE::BSScript::Variable& self) {
            auto obj = self.GetObject();
            if (!obj) return {};
            // A magic effect script runs on an active effect, not a form: trace it to its spell (or the effect's base form).
            if (auto* active = static_cast<RE::ActiveEffect*>(obj->Resolve(RE::ActiveEffect::VMTYPEID))) {
                const RE::TESForm* source = active->spell ? static_cast<const RE::TESForm*>(active->spell) : active->GetBaseObject();
                if (source) {
                    if (auto text = Describe(source); !text.empty()) return text;
                }
                return "(magic effect, source not found)";
            }
            const auto handle = obj->GetHandle();
            const auto formId = static_cast<RE::FormID>(handle & 0xFFFFFFFF);
            auto* form = formId ? RE::TESForm::LookupByID(formId) : nullptr;
            if (!form) {
                // OnEffectFinish and similar run after the effect is gone, so nothing is left to trace.
                const auto* type = obj->GetTypeInfo();
                const char* script = type ? type->GetName() : nullptr;
                return std::string("(owner gone") + (script && script[0] ? std::string(": ") + script : "") + ")";
            }
            if (auto text = Describe(form); !text.empty()) return text;
            return (formId >> 24) == 0xFF ? "(created in game)" : "";
        }

        // FEAT-268: the call a queued function message will make, as "Script.Function (queued)", plus its owner.
        std::pair<std::string, std::string> DescribeQuery(RE::BSScript::Internal::IFuncCallQuery* query) {
            if (!query) return {};
            RE::BSScript::Internal::IFuncCallQuery::CallType callType{};
            RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> type;
            RE::BSFixedString fn;
            RE::BSScript::Variable self;
            RE::BSScrapArray<RE::BSScript::Variable> args;
            if (!query->GetFunctionCallInfo(callType, type, fn, self, args)) return {};
            const char* t = type ? type->GetName() : nullptr;
            return {std::string(t && t[0] ? t : "?") + "." + (fn.c_str() && fn.c_str()[0] ? fn.c_str() : "?") + " (queued)", OwnerOf(self)};
        }

        // Every call waiting to start: the function message queue (and its overflow) and both suspend queues (and theirs).
        // A queued stack has no frames, so the call is the only place its function and object are known (FEAT-268).
        template <class F>
        std::pair<std::size_t, std::size_t> ForEachPendingCall(RE::BSScript::Internal::VirtualMachine* impl, F&& visit) {
            std::size_t messages = 0, suspended = 0;
            for (auto* elem = impl->funcMsgQueue.head; elem && messages < 200000; elem = elem->next, ++messages) {
                auto* msg = reinterpret_cast<RE::BSScript::Internal::FunctionMessage*>(elem->elem);
                if (msg && msg->stack) visit(msg->stack.get(), msg->funcQuery.get());
            }
            for (auto& msg : impl->overflowFuncMsgs) {
                if (++messages > 200000) break;
                if (msg.stack) visit(msg.stack.get(), msg.funcQuery.get());
            }
            // The retest found none of the ~11,000 queued stacks in the function message queue: stacks waiting to start (or
            // to resume) sit in the suspend queues instead, each with its call. Walk both ring buffers and their overflow arrays.
            RE::BSSpinLockGuard suspendGuard(impl->suspendQueueLock);
            auto walkRing = [&](const auto& queue) {
                constexpr std::uint32_t kSize = 128;
                const auto* entries = reinterpret_cast<const RE::BSScript::Internal::SuspendedStack*>(queue.queueBuffer);
                const auto count = std::min<std::uint32_t>(queue.numEntries, kSize);
                for (std::uint32_t i = 0; i < count; ++i) {
                    const auto& e = entries[(queue.popIdx + i) % kSize];
                    ++suspended;
                    if (e.stack) visit(e.stack.get(), e.funcCallQuery.get());
                }
            };
            walkRing(impl->suspendQueue1);
            walkRing(impl->suspendQueue2);
            for (const auto* arr : {&impl->overflowSuspendArray1, &impl->overflowSuspendArray2}) {
                for (const auto& e : *arr) {
                    if (++suspended > 200000) break;
                    if (e.stack) visit(e.stack.get(), e.funcCallQuery.get());
                }
            }
            return {messages, suspended};
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
        std::map<std::string, std::map<std::string, int>> entryOwners;
        std::size_t running = 0, latent = 0, queued = 0;
        // FEAT-268: pending function messages (the VM's queue and its overflow) hold the stack each call will run on and the
        // call itself; a queued stack has no frames, so this is where its name comes from (the Aniya flood: ~11,000 queued).
        std::unordered_map<const RE::BSScript::Stack*, std::pair<std::string, std::string>> queuedCalls;
        const auto [pendingMessages, suspendedWalked] = ForEachPendingCall(impl, [&](const RE::BSScript::Stack* stack, RE::BSScript::Internal::IFuncCallQuery* query) {
            queuedCalls[stack] = DescribeQuery(query);
        });
        std::size_t queuedNamed = 0;
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
                if (!topFrame) {
                    // FEAT-268: a queued stack has no frames yet (14,000 of them during the Aniya flood went unnamed). Its tasklet
                    // knows the call it will make. (The whole task runs inside the game-thread fault guard.)
                    ++queued;
                    std::string name = "(queued, function unknown)";
                    std::string owner;
                    if (auto it = queuedCalls.find(stack); it != queuedCalls.end() && !it->second.first.empty()) {
                        ++queuedNamed;
                        name = it->second.first;
                        owner = it->second.second;
                    } else if (auto* tasklet = stack->owningTasklet.get()) {
                        RE::BSScript::Internal::IFuncCallQuery::CallType callType{};
                        RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> type;
                        RE::BSFixedString fn;
                        RE::BSScript::Variable self;
                        RE::BSScrapArray<RE::BSScript::Variable> args;
                        if (tasklet->GetFunctionCallInfo(callType, type, fn, self, args)) {
                            const char* t = type ? type->GetName() : nullptr;
                            name = std::string(t && t[0] ? t : "?") + "." + (fn.c_str() && fn.c_str()[0] ? fn.c_str() : "?") + " (queued)";
                            owner = OwnerOf(self);
                        }
                    }
                    ++byEntryPoint[name];
                    if (!owner.empty()) ++entryOwners[name][owner];
                    continue;
                }
                ++byCurrentFunction[FrameName(topFrame)];
                // The bottom frame is where the call started: usually an event in the mod's own script.
                const auto* bottom = topFrame;
                std::set<std::string> scripts;
                for (int depth = 0; bottom && depth < 256; ++depth) {
                    scripts.insert(ScriptOf(bottom));
                    if (!bottom->previousFrame) break;
                    bottom = bottom->previousFrame;
                }
                const auto entry = FrameName(bottom);
                ++byEntryPoint[entry];
                if (auto owner = OwnerPlugin(bottom); !owner.empty()) ++entryOwners[entry][owner];
                for (const auto& s : scripts) ++byScript[s];
            }
        }

        result["runningStacks"] = running;
        result["queuedStacks"] = queued;
        // Diagnostics for naming queued stacks: how many entries each source had, and how many queued stacks matched one.
        result["queuedDiagnostics"] = {{"functionMessages", pendingMessages}, {"suspendedEntries", suspendedWalked},
                                       {"callsFound", queuedCalls.size()}, {"queuedNamed", queuedNamed}};
        result["waitingLatentReturns"] = latent;
        result["stacksByState"] = byState;
        result["topScripts"] = TopN(byScript, top);
        result["topEntryPoints"] = TopN(byEntryPoint, top);
        result["topCurrentFunctions"] = TopN(byCurrentFunction, top);
        // FEAT-268: the plugins that own the objects each entry point runs on (most common first).
        json owners = json::object();
        for (const auto& [entry, plugins] : entryOwners) {
            std::vector<std::pair<std::string, int>> sorted(plugins.begin(), plugins.end());
            std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.second > b.second; });
            // FEAT-268: up to 5 by name, then the rest summed, so the counts add up (37 MCM menus each own one stack).
            json list = json::array();
            int rest = 0, restPlugins = 0;
            for (std::size_t i = 0; i < sorted.size(); ++i) {
                if (i < 5) list.push_back({{"plugin", sorted[i].first}, {"stacks", sorted[i].second}});
                else { rest += sorted[i].second; ++restPlugins; }
            }
            if (restPlugins > 0) list.push_back({{"plugin", std::format("{} more plugins", restPlugins)}, {"stacks", rest}});
            owners[entry] = list;
        }
        result["entryPointOwners"] = owners;
        result["note"] =
            "A snapshot. Stacks waiting on a latent function (Utility.Wait, RegisterForSingleUpdate loops) are normal in small numbers; "
            "hundreds of stacks from one script, a high waitingFunctionMessages count or overstressed=true point to that mod as the cause of script lag. "
            "topEntryPoints is where each call started (usually the mod's own event handler).";
        return result;
    }

    json FindStackObjects(const std::string& entry, int limit) {
        limit = std::clamp(limit, 1, 500);
        auto* vm = RE::SkyrimVM::GetSingleton();
        auto* impl = vm ? static_cast<RE::BSScript::Internal::VirtualMachine*>(vm->GetVMRuntimeData().impl.get()) : nullptr;
        if (!impl) return {{"error", "The Papyrus VM is not available yet"}};

        // "Script.Function" matches that handler (queued or running); "Script" alone matches every handler of the script.
        auto lower = [](std::string s) { std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); }); return s; };
        std::string want = lower(entry);
        if (auto cut = want.find(" (queued)"); cut != std::string::npos) want.erase(cut);
        const bool scriptOnly = want.find('.') == std::string::npos;
        auto matches = [&](const std::string& name) {
            auto n = lower(name);
            if (auto cut = n.find(" (queued)"); cut != std::string::npos) n.erase(cut);
            return scriptOnly ? n.rfind(want + ".", 0) == 0 : n == want;
        };

        struct Found { std::string type; std::string owner; RE::FormID formId = 0; int running = 0; int queued = 0; };
        std::map<RE::VMHandle, Found> objects;
        auto note = [&](const RE::BSScript::Variable& self, bool queued) {
            auto obj = self.GetObject();
            if (!obj) return;
            auto& f = objects[obj->GetHandle()];
            if (f.type.empty()) {
                const auto* t = obj->GetTypeInfo();
                f.type = t && t->GetName() ? t->GetName() : "";
                f.owner = OwnerOf(self);
                f.formId = static_cast<RE::FormID>(obj->GetHandle() & 0xFFFFFFFF);
            }
            ++(queued ? f.queued : f.running);
        };

        ForEachPendingCall(impl, [&](const RE::BSScript::Stack*, RE::BSScript::Internal::IFuncCallQuery* query) {
            if (!query) return;
            RE::BSScript::Internal::IFuncCallQuery::CallType callType{};
            RE::BSTSmartPointer<RE::BSScript::ObjectTypeInfo> type;
            RE::BSFixedString fn;
            RE::BSScript::Variable self;
            RE::BSScrapArray<RE::BSScript::Variable> args;
            if (!query->GetFunctionCallInfo(callType, type, fn, self, args)) return;
            const char* t = type ? type->GetName() : nullptr;
            if (matches(std::string(t ? t : "") + "." + (fn.c_str() ? fn.c_str() : ""))) note(self, true);
        });
        {
            RE::BSSpinLockGuard guard(impl->runningStacksLock);
            if (impl->allRunningStacks.size() > 200000) return {{"error", "Papyrus VM data doesn't look valid for this game version"}};
            for (const auto& item : impl->allRunningStacks) {
                const auto* stack = item.second.get();
                const auto* bottom = stack ? stack->top : nullptr;
                if (!bottom) continue;   // queued: counted from its call above
                for (int depth = 0; bottom->previousFrame && depth < 256; ++depth) bottom = bottom->previousFrame;
                if (matches(FrameName(bottom))) note(bottom->self, false);
            }
        }

        std::vector<std::pair<RE::VMHandle, Found>> sorted(objects.begin(), objects.end());
        std::sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) { return a.second.running + a.second.queued > b.second.running + b.second.queued; });
        json list = json::array();
        for (const auto& [handle, f] : sorted) {
            if (static_cast<int>(list.size()) >= limit) break;
            json o = {{"handle", std::format("{:016X}", handle)}, {"script", f.type}, {"owner", f.owner},
                      {"running", f.running}, {"queued", f.queued}};
            if (auto* form = f.formId ? RE::TESForm::LookupByID(f.formId) : nullptr) {
                o["formId"] = std::format("{:08X}", f.formId);
                o["formType"] = RE::FormTypeToString(form->GetFormType());
                const RE::TESForm* named = form;
                if (auto* ref = form->As<RE::TESObjectREFR>()) {
                    if (const char* n = ref->GetDisplayFullName(); n && n[0]) o["name"] = n;
                    if (auto* base = ref->GetBaseObject()) {
                        named = base;
                        o["baseFormId"] = std::format("{:08X}", base->GetFormID());
                        o["baseType"] = RE::FormTypeToString(base->GetFormType());
                    }
                }
                if (!o.contains("name")) { if (const char* n = named->GetName(); n && n[0]) o["name"] = n; }
                if (const char* e = named->GetFormEditorID(); e && e[0]) o["editorId"] = e;
            }
            list.push_back(std::move(o));
        }
        return {{"entry", entry}, {"objects", list}, {"objectCount", objects.size()}};
    }

    // FEAT-280 fix: StopRunaway's UnregisterForUpdate calls went through the script engine and timed out at 5 s each in game:
    // they queue behind the very pile-up they are meant to stop. The registrations are an array the VM keeps by object handle
    // (what UnregisterForUpdate edits); removing them here, on the game thread under the VM's own lock, stops the next
    // OnUpdate at once. Stacks already queued still run once.
    json UnregisterUpdates(const std::vector<std::uint64_t>& handles, bool gameTime) {
        auto* vm = RE::SkyrimVM::GetSingleton();
        if (!vm) return {{"error", "The Papyrus VM is not available yet"}};
        auto& data = vm->GetVMRuntimeData();
        const std::set<RE::VMHandle> wanted(handles.begin(), handles.end());
        auto prune = [&](RE::BSTArray<RE::BSTSmartPointer<RE::SkyrimVM::UpdateDataEvent>>& regs, int& removed) -> bool {
            // A count this large means the layout doesn't match this game version: leave it alone.
            if (regs.size() > 500000) return false;
            for (auto i = regs.size(); i-- > 0;) {
                const auto& r = regs[i];
                if (r && wanted.contains(r->handle)) {
                    regs.erase(regs.begin() + i);
                    ++removed;
                }
            }
            return true;
        };
        int removed = 0, removedGameTime = 0;
        std::size_t before = 0, beforeGameTime = 0;
        bool ok = true;
        {
            RE::BSSpinLockGuard guard(data.queuedOnUpdateEventLock);
            before = data.queuedOnUpdateEvents.size();
            ok = prune(data.queuedOnUpdateEvents, removed);
            if (ok && gameTime) {
                beforeGameTime = data.queuedOnUpdateGameEvents.size();
                ok = prune(data.queuedOnUpdateGameEvents, removedGameTime);
            }
        }
        if (!ok) return {{"error", "The VM's update registrations don't look valid for this game version; nothing was changed."}};
        json result = {{"registrationsBefore", before}, {"removed", removed}};
        if (gameTime) { result["gameTimeRegistrationsBefore"] = beforeGameTime; result["removedGameTime"] = removedGameTime; }
        return result;
    }

}
