    this->post_motion_features = [this](const server_http_req & req) {
        auto res = create_response();
        const json body = json::parse(req.body);
        if (!body.is_object() || !body.contains("text") || !body["text"].is_string() ||
            !body.contains("feature_contract") || body["feature_contract"] != "f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0" ||
            (body.contains("request_id") && !body["request_id"].is_string())) {
            res->error(format_error_response("Expected text, request_id, and the exact trained feature_contract hash", ERROR_TYPE_INVALID_REQUEST));
            return res;
        }
        const std::string text = body["text"].get<std::string>();
        const std::string request_id = body.value("request_id", std::string());
        if (text.empty() || text.size() > 1024 || request_id.size() > 128 || text.find('\0') != std::string::npos) {
            res->error(format_error_response("Motion text must be 1..1024 UTF-8 bytes and request_id at most 128 bytes", ERROR_TYPE_INVALID_REQUEST));
            return res;
        }
        server_task task(SERVER_TASK_TYPE_MOTION_FEATURE);
        task.id = res->rd.get_new_id();
        task.motion_text = text;
        task.motion_request_id = request_id;
        task.motion_submitted_ms = ggml_time_ms();
        res->rd.post_task(std::move(task));
        auto result = res->rd.next(req.should_stop);
        if (!result) { return res; }
        if (result->is_error()) { res->error(result->to_json()); return res; }
        res->ok(result->to_json());
        return res;
    };
