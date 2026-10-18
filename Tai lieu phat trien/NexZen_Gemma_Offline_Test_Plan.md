# NexZen Desktop — Gemma Offline AI Test Plan

**Version:** 0.1  
**Status:** Proposal for review  
**Scope:** NexZen Desktop (Avalonia + CEF), reusing the existing AI Hub Custom OpenAI-compatible endpoint.

## 1. Goal

Test whether a Gemma instruction-tuned model can run locally for web-page summarization and page-grounded Q&A, including Vietnamese and English, without sending prompts or page content to a cloud AI provider.

## 2. Proposed architecture

```text
NexZen AI Hub
  -> Custom provider
  -> http://127.0.0.1:8080/v1/chat/completions
  -> Local inference server (initial candidate: llama.cpp)
  -> Gemma GGUF model
```

Reuse the current Custom provider first. Do not implement a new provider unless testing reveals a genuine incompatibility. Bind the local server to `127.0.0.1`, not all network interfaces.

## 3. Model candidates

| Candidate | Purpose | Notes |
|---|---|---|
| Gemma 3 4B IT, quantized GGUF in Q4 class | Primary quality candidate | Test first for multilingual summary and Q&A. Memory use depends on quantization, context, runtime and backend. |
| Gemma 3 1B IT, compatible quantized build | Lightweight comparison | Test if 4B is too slow or memory-heavy; quality may be lower. |

References:
- https://huggingface.co/google/gemma-3-4b-it
- https://huggingface.co/google/gemma-3-1b-it
- https://github.com/ggml-org/llama.cpp

Verify the exact artifact, runtime compatibility and applicable license before downloading or redistributing model files. Prefer downloading model weights separately rather than bundling several GB in the NexZen installer.

## 4. Custom provider settings

Create a separate provider entry so existing cloud settings remain unchanged.

| Field | Initial value |
|---|---|
| Name | `Gemma Local` |
| Base URL | `http://127.0.0.1:8080` |
| Model | Exact identifier accepted by the local server |
| API key | Blank if supported; otherwise a dummy value only if the client/server requires it |

If NexZen appends `/v1/chat/completions`, do not include `/v1` in the Base URL. Verify empty-key handling, streaming, cancellation, timeouts, model identifier handling, and errors when the server is stopped. Never put a real cloud API key into this local profile.

## 5. Phased plan

### Phase 0 — Baseline and prerequisites
- [ ] Record OS, CPU, RAM, GPU, driver and runtime versions.
- [ ] Confirm Custom provider behavior and whether it supports streaming/cancellation.
- [ ] Choose a compatible GGUF artifact and runtime build.
- [ ] Record model file size and license.

**Exit:** compatibility and rollback path are understood.

### Phase 1 — Run the model independently
- [ ] Start the server bound to `127.0.0.1`.
- [ ] Query the model-list endpoint.
- [ ] Send a short Chat Completions request.
- [ ] Check Vietnamese and English responses.
- [ ] After downloading all required files, disconnect from the internet and repeat.

**Exit:** inference works offline without a cloud AI API.

### Phase 2 — Connect to NexZen
- [ ] Add `Gemma Local` as a Custom provider.
- [ ] Test a normal response and streaming if supported.
- [ ] Test cancellation, timeout, server-unavailable and model-loading errors.
- [ ] Switch between Gemma Local and existing cloud providers; verify saved settings remain intact.

**Exit:** the existing AI Hub UI can use the model without a dedicated provider implementation.

### Phase 3 — Summarization quality
Use a fixed test set and identical instructions for each model:
- [ ] Vietnamese news article.
- [ ] English technology article.
- [ ] Long technical documentation.
- [ ] Page with navigation, ads, related links and repeated text.
- [ ] Long page that exceeds the model's comfortable context.

Assess factual accuracy, retention of names/numbers/dates, output language, conciseness, hallucination rate and long-page handling.

### Phase 4 — Page Q&A and grounding
- [ ] Ask questions answered explicitly by the page.
- [ ] Ask a question whose answer is absent; model should say the page does not provide enough information.
- [ ] Request a supporting quote or section.
- [ ] Include instruction-like text in the page and verify it is treated as untrusted content, not as instructions to the assistant.

**Exit:** answers are grounded in the supplied page content and uncertainty is handled honestly.

### Phase 5 — Performance and stability
Measure on the actual target computer:
- [ ] Model load time and time to first token.
- [ ] Generation speed (tokens/sec).
- [ ] Peak RAM and, if applicable, VRAM/shared GPU memory.
- [ ] CPU/GPU utilization.
- [ ] Browser responsiveness with several tabs open.
- [ ] Repeated requests, cancellation, shutdown and restart behavior.
- [ ] Quality and memory at two context settings.

Do not set hard performance targets until hardware and baseline measurements are known.

### Phase 6 — UX and release hardening
- [ ] Show runtime state: stopped, starting, ready, busy or error.
- [ ] Explain missing model/runtime and out-of-memory errors.
- [ ] Provide a stop-generation action.
- [ ] Explain that model weights are downloaded separately and may occupy several GB.
- [ ] Avoid logging page content, prompts, responses or keys by default.
- [ ] Document tested model/runtime versions and license.
- [ ] Keep cloud providers usable when the local runtime is unavailable.

## 6. Example startup command

Template only: replace the path with the actual model file and verify flags for the installed llama.cpp version.

```bash
llama-server \
  -m /path/to/gemma-model.gguf \
  --host 127.0.0.1 \
  --port 8080 \
  -c 8192
```

Start with a moderate context size, then tune it after measuring quality and memory. If the model fails to load, check the exact GGUF conversion and runtime version.

Connectivity check:

```bash
curl http://127.0.0.1:8080/v1/models
```

Use the exact model identifier accepted by the server in subsequent `POST /v1/chat/completions` requests.

## 7. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Model/runtime incompatibility | Pin tested versions and validate the exact GGUF artifact. |
| Slow inference or high memory use | Compare 4B Q4 with 1B; tune context; unload when idle if supported. |
| Weak long-page summary | Extract main content, chunk carefully, summarize chunks and synthesize a final result. |
| Hallucinated answers | Ground prompts in page content; allow “not stated”; test absent-answer cases. |
| Prompt injection in web pages | Treat page text as untrusted data; never let it authorize external actions. |
| Local endpoint exposed to LAN | Bind only to loopback. |
| Regressions in existing providers | Keep Gemma in a separate Custom entry and run cloud-provider regression tests. |

## 8. Acceptance criteria

The experiment is successful when:
1. Gemma runs without a cloud API key.
2. NexZen AI Hub calls it through the existing Custom endpoint.
3. Vietnamese and English summaries are useful and factually faithful on the test set.
4. Page Q&A distinguishes page-supported answers from missing information.
5. RAM, latency and token speed are measured on the target machine.
6. Existing cloud providers continue to work after switching back.

## 9. Decisions after testing

- Choose 4B as recommended local model or offer 1B for lower-memory machines.
- Decide whether NexZen manages the runtime process or asks users to start it.
- Decide whether NexZen manages model downloads/updates.
- Decide when to add long-page chunking and discussion analysis.
- Define officially supported OS/GPU backends from measured results.

## 10. Out of scope for the first experiment

- Fine-tuning or training a model.
- Bundling model weights in the installer.
- Rebuilding the provider system before testing Custom compatibility.
- Forum-wide majority/minority analysis.
- Promising identical performance across hardware.

---

**Recommended first action:** run a compatible Gemma GGUF using a local OpenAI-compatible server, register it as `Gemma Local`, then verify a short Vietnamese summary before adding runtime-management UI.
