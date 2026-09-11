    // Included inside server_context_impl. These operations run exclusively in
    // the server inference loop, so chat and action never decode concurrently.
    bool init_motion_context() {
        const char * enabled = std::getenv("NEEEVA_MOTION_FEATURES");
        if (!enabled || std::string(enabled) != "1") { return true; }
        const char * verified = std::getenv("NEEEVA_MOTION_MODEL_SHA256");
        if (!verified || std::string(verified) != "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4") {
            SRV_ERR("%s", "Motion feature startup requires the verified model SHA256 from the launcher\n"); return false;
        }
        if (llama_model_n_embd(model) != 2048) { return true; }
        auto cp = llama_context_default_params();
        cp.n_ctx = 512; cp.n_batch = 512; cp.n_ubatch = 512; cp.n_seq_max = 1;
        cp.n_threads = 8; cp.n_threads_batch = 8;
        cp.embeddings = true; cp.pooling_type = LLAMA_POOLING_TYPE_NONE;
        motion_ctx = llama_init_from_model(model, cp);
        if (!motion_ctx) { SRV_ERR("%s", "Cannot initialize shared-model action context\n"); return false; }
        SRV_INF("shared action model=%p chat_model=%p action_model=%p ctx=512 slots_unchanged=true\n",
            (void *) model, (void *) llama_get_model(ctx), (void *) llama_get_model(motion_ctx));
        return llama_get_model(ctx) == llama_get_model(motion_ctx);
    }

    void expire_motion_request() {
        if (motion_pending && ggml_time_ms() - motion_pending->motion_submitted_ms >= 15000) {
            send_error(*motion_pending, "Motion feature expired while waiting for idle chat slots", ERROR_TYPE_UNAVAILABLE);
            motion_pending.reset();
        }
    }

    void process_motion_request() {
        if (!motion_pending) { return; }
        expire_motion_request();
        if (!motion_pending) { return; }
        auto task = std::move(motion_pending);
        const int64_t started = ggml_time_ms();
        const std::string prompt = "Motion description: " + task->motion_text + "\nRepresentation:";
        auto ids = common_tokenize(vocab, prompt, true, true);
        if (ids.empty() || ids.size() > 500) {
            send_error(*task, "Motion template exceeds 500 input tokens; it is never truncated", ERROR_TYPE_INVALID_REQUEST);
            return;
        }
        // Clear only the separate feature ctx, including hybrid recurrent state.
        llama_memory_clear(llama_get_memory(motion_ctx), true);
        auto mb = llama_batch_init((int) ids.size(), 0, 1);
        mb.n_tokens = (int) ids.size();
        for (int i = 0; i < mb.n_tokens; ++i) {
            mb.token[i] = ids[i]; mb.pos[i] = i; mb.n_seq_id[i] = 1;
            mb.seq_id[i][0] = 0; mb.logits[i] = i == mb.n_tokens - 1;
        }
        const int decoded = llama_decode(motion_ctx, mb);
        llama_batch_free(mb);
        const float * output = decoded == 0 ? llama_get_embeddings_ith(motion_ctx, -1) : nullptr;
        if (!output) {
            llama_memory_clear(llama_get_memory(motion_ctx), true);
            send_error(*task, "Motion feature decode failed", ERROR_TYPE_SERVER);
            return;
        }
        std::vector<float> embedding(output, output + 2048);
        llama_memory_clear(llama_get_memory(motion_ctx), true);
        for (float v : embedding) {
            if (!std::isfinite(v)) { send_error(*task, "Nonfinite motion feature", ERROR_TYPE_SERVER); return; }
        }
        auto result = std::make_unique<server_task_result_motion_feature>();
        result->id = task->id;
        result->data = {
            {"feature_contract", "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0"},
            {"protocol", "qwen-motion-raw-last-v1"}, {"feature_source", "live-qwen"},
            {"request_id", task->motion_request_id}, {"text", task->motion_text},
            {"embedding", embedding}, {"dimension", 2048}, {"pooling", "last_input_token_raw"},
            {"template", "Motion description: {text}\nRepresentation:"},
            {"shared_model", llama_get_model(ctx) == llama_get_model(motion_ctx)},
            {"model_path", params_base.model.path},
            {"model_sha256", "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"},
            {"llama_cpp_revision", "dc80c5252a6d49301726e5987cc1fa006b69d93e"},
            {"context_tokens", 512}, {"input_tokens", ids.size()},
            {"chat_slots", slots.size()}, {"chat_context_tokens_per_slot", llama_n_ctx_seq(ctx)},
            {"timings", {{"queue_ms", started - task->motion_submitted_ms}, {"encode_ms", ggml_time_ms() - started}}}
        };
        queue_results.send(std::move(result));
    }
