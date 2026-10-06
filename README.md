# Codexを使用して製作した成果物一覧

このRepositoryには、Codexを使用して製作した成果物を、元の完成状態を尊重して保存します。

## Limiter（HipHopMax v1.0.0）

Windows x64 / VST3向けの個人使用ステレオ・マキシマイザーです。ソースコード、テスト、CMake設定、検証記録は [`Limiter/`](Limiter/) にあります。

詳細は [Limiter/README.md](Limiter/README.md) を参照してください。

## Workout Tracker

Windowsデスクトップ向けのWorkout記録アプリです。現在も継続開発中のため、完成版Releaseではなく、確認時点の開発スナップショットとして [WorkoutTracker/](WorkoutTracker/) に保存しています。

主な領域は、Workout記録、Active DraftのAutosave/Rehydrate、履歴・カレンダー、Dashboard、Statistics、SQLite/EF Coreによる永続化、Backup/Restore、Migration、WPFテーマ、.NET MAUI Android向けのローカル構成、自動テストです。実装範囲と未確定の検証項目は [WorkoutTracker/README.md](WorkoutTracker/README.md) に記載しています。

コードの大部分はAIエージェントを中心に実装しています。ユーザー本人は要件整理、設計判断、レビュー、テスト、実機確認、コード理解を行いながら開発しています。
