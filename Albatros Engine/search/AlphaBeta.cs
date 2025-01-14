﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
class AlphaBeta
{
    public zobrist_hash hash = new zobrist_hash();
    Stopwatch sw = new Stopwatch();
    Semaphore time_acces = new Semaphore(1, 1), depth_acces = new Semaphore(1, 1);
    // StreamWriter stream_writer;
    bool stop = false;
    int[] sorting_counter = new int[300];

    public ulong[] repetitions = new ulong[307];
    bool[] repetion_lookup = new bool[ushort.MaxValue];

    standart stuff = new standart();
    standart_chess chess_stuff = new standart_chess();
    public NNUE_avx2 ValueNet;
    public movegen MoveGenerator = new movegen();
    public Classic_Eval eval = new Classic_Eval();
    public Move_Ordering_Heuristics move_order = new Move_Ordering_Heuristics();
    Random random = new Random(59675943);
    int Nodecount = 0, max_ply = 0, move_counter = 0, root_depth = 1;
    int[,,] move_reductions = new int[2, 64, 64];
    bool[] null_move_pruning = new bool[byte.MaxValue + 1];
    public const int mate_value = 60000;
    public const int illegal_position_value = 80000;
    public const int max_depth = 127;

    int[] node_values = new int[byte.MaxValue + 1];
    public long time_to_use = 0;
    public byte[,] HashTable = new byte[0, 0];

    public AlphaBeta(int HashSize)
    {
        HashTable = new byte[HashSize * 62500, 16];
        //init_reductions(0, 0, 5, 4, 3, 3, -0.2, 0.5);
        reduction_b();
        ValueNet = new NNUE_avx2(true);
    }
    public void Stop()
    {
        stop = true;
    }
    public int TimedAlphaBeta(long Milliseconds, position InputBoard, bool NNUE_avx2, bool change_time)
    {
        time_to_use = Milliseconds;
        Thread timer = new Thread(ThreadTimer);
        timer.Start();
        return iterative_deepening(InputBoard, max_depth, NNUE_avx2, change_time);
    }
    public void ThreadTimer()
    {
        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();
        long time_to_search = time_to_use;
        int search_depth = 1;
        while (stopwatch.ElapsedMilliseconds <= time_to_search || search_depth == 1)
        {
            Thread.Sleep(1);
            depth_acces.WaitOne();
            search_depth = root_depth;
            depth_acces.Release();

            time_acces.WaitOne();
            time_to_search = time_to_use;
            time_acces.Release();
        }
        Stop();
    }
    public int iterative_deepening(position board, int depth, bool NNUE_avx2, bool change_time)
    {
        //initialize the variables
        move_order.reset_movesort();
        int[] movelist = new int[218];
        reverse_move undo_move = new reverse_move();
        stop = false;
        byte othercolor = (byte)(board.color ^ 1);
        List<int[]> last_best_moves = new List<int[]>();
        bool search_pv = true, in_check = MoveGenerator.check(board, false), gives_check = false;
        int Output = 0, new_depth = 0;
        movelist = MoveGenerator.legal_move_generator(board, in_check, undo_move, movelist);
        int movelist_length = MoveGenerator.move_idx;
        pv_out current_variation = new pv_out(), pv = new pv_out();
        int alpha = -illegal_position_value, delta_a = 0, delta_b = 0, window_a = 0, window_b = 0, last_best = 0, last_last_best = 0, current_score = 0;
        bool pruning_is_safe = false;
        Accumulator currentacc = null;
        if (NNUE_avx2) currentacc = new Accumulator(16);
        move_and_eval_list move_list = new move_and_eval_list();
        time_acces.WaitOne();
        long theoretical_time_usage = time_to_use;
        time_acces.Release();
        //get the key for the position
        ulong key = hash.hash_position(board);

        //start the stopwatch
        sw.Start();
        sw.Start();

        if (NNUE_avx2)
        {
            //the the accumulator position to the starting position
            ValueNet.set_acc_from_position(board);

            //copy the accumulator for the current position
            currentacc = chess_stuff.acc_copy(ValueNet.acc);
            current_score = ValueNet.AccToOutput(ValueNet.acc, board.color);
        }
        else
            current_score = eval.pesto_eval(board);

        pruning_is_safe = !in_check && MoveGenerator.non_pawn_material;

        node_values[0] = !in_check ? current_score : illegal_position_value;

        for (int current_depth = 1; current_depth <= depth; current_depth++)
        {
            depth_acces.WaitOne();
            root_depth = current_depth;
            depth_acces.Release();
            node_values[0] = !in_check ? (IsvalidEntry(key) == 1 ? GetInfoFromEntry(key).Score : current_score) : illegal_position_value;

            if (current_depth >= 4 && Math.Abs(last_last_best) < mate_value)
            {
                //if the current depth is larger then 2 reajust the window
                delta_a = -125;
                window_a = last_last_best + delta_a;
                delta_b = 125;
                window_b = last_last_best + delta_b;
                alpha = window_a;
            }
            else
            {
                window_a = -illegal_position_value;
                window_b = illegal_position_value;
                alpha = window_a;
            }

            while (!stop)
            {
                search_pv = true;

                move_list = move_order.evaluate_moves(board, stuff.copy_int_array(movelist), movelist_length, 0, false, IsvalidEntry(key) == 1 ? GetInfoFromEntry(key).BestMove : 0, move_list);
                while (move_list.movelist_length > 0)
                {

                    movepick current_move = move_order.pick_next_move(move_list);
                    //Debug.Assert(!stuff.int_array_equal(current_move.move, new int[] { 4, 5, 4, 4 }) || current_depth != 3);

                    current_variation = new pv_out();
                    move_order.add_current_move(current_move.move, board, 0);

                    //play the move
                    board = make_move(board, current_move.move, NNUE_avx2, undo_move);

                    //get the hash key
                    ulong new_key = hash.update_position_hash_after_move(board, undo_move, key);

                    //calculate if the current move gives check
                    gives_check = MoveGenerator.fast_check(board, current_move.move);

                    //find if the current position is a terminal position
                    //determining the mate value 2 => not a terminal position , 0 => draw , 1 => mate for white , -1 => mate for black
                    int matingValue = MoveGenerator.is_mate(board, gives_check, new reverse_move(), new int[214]);

                    new_depth = current_depth - 1;

                    //checking if the position is not a terminal node
                    if (matingValue != 2)
                    {
                        //if the position is a terminal node the value for the node is set to the mating value from the perspective of the current color
                        current_variation.value = matingValue == 0 ? 0 : mate_value + max_depth;
                        current_variation.principalVariation.Insert(0, current_move.move);
                    }
                    else
                    {
                        //if the current depth is 1 perform a quiescent search
                        if (new_depth <= 0)
                        {
                            current_variation.value = -quiescence_search(board, -window_b, -alpha, NNUE_avx2, 0, 1, new_key, gives_check);

                            current_variation.principalVariation.Add(current_move.move);
                        }
                        //else perform a normal pv search
                        else
                        {
                            //perform a pv search
                            if (search_pv)
                            {
                                current_variation = principal_variation_search(board, new_depth, 1, -window_b, -alpha, gives_check, NNUE_avx2, new_key);
                                current_variation.value = -current_variation.value;
                                current_variation.principalVariation.Insert(0, current_move.move);
                            }
                            else
                            {
                                current_variation.value = -zero_window_search(board, new_depth, 1, -(alpha + 1), -alpha, gives_check, NNUE_avx2, new_key);

                                if (current_variation.value > alpha && current_variation.value < window_b)
                                {
                                    current_variation = principal_variation_search(board, new_depth, 1, -window_b, -alpha, gives_check, NNUE_avx2, new_key);
                                    current_variation.value = -current_variation.value;
                                    current_variation.principalVariation.Insert(0, current_move.move);
                                }
                            }
                        }
                    }

                    //undo the move
                    board = unmake_move(board, undo_move, currentacc, NNUE_avx2);

                    if (alpha < current_variation.value)
                    {
                        if (chess_stuff.is_capture(current_move.move, board))
                            move_order.update_history_move(board, current_move.move, 0, 0, 0, 0, Math.Min((float)(depth * depth) / 10f, 40), 0);
                        else
                            move_order.update_chistory_move(board, current_move.move, Math.Min((float)(depth * depth) / 10f, 40));

                        alpha = current_variation.value;
                        pv = current_variation;
                        search_pv = false;
                    }

                    if (stop || alpha >= window_b)
                        break;
                }

                if (alpha <= window_a)
                {
                    delta_a *= 2;
                    window_a = last_last_best + delta_a;
                    alpha = window_a;
                }
                else if (alpha >= window_b)
                {
                    delta_b *= 2;
                    alpha = window_a;
                    window_b = last_last_best + delta_b;
                }
                else
                    break;
            }

            if (!stop)
            {
                Output = pv.principalVariation[0];

                //adjust timing
                time_acces.WaitOne();
                if (current_depth > 2 && change_time)
                {
                    //if the timing is already maximal do not change it
                    //else if th bestmove is different to the last best move make the time usage larger
                    if (theoretical_time_usage * 14 > time_to_use * 10 && Output != GetInfoFromEntry(key).BestMove && theoretical_time_usage < 500)
                        time_to_use += theoretical_time_usage / 10;
                    //else if it is really low do not change it
                    //else make the time usage smaller
                    else if (theoretical_time_usage * 4 < time_to_use * 10)
                        time_to_use -= theoretical_time_usage / 10;
                }
                time_acces.Release();

                //add the best move to the hash table
                AddToTable(Output, current_depth, alpha, key, 0, 0);
            }
            //after a finished search return the main informations 
            if (!stop)
            {
                if (Math.Abs(alpha) < mate_value)
                    Console.WriteLine("info depth {2} seldepth {3} nodes {1} nps {4} time {5} score cp {0} pv {6}", alpha / 10, Nodecount, current_depth, current_depth + max_ply, (int)(((float)(Nodecount) * 1000) / (sw.ElapsedMilliseconds > 0 ? (float)sw.ElapsedMilliseconds : 1)), (int)(sw.ElapsedMilliseconds), variation_to_string(pv.principalVariation));
                else
                    Console.WriteLine("info depth {2} seldepth {3} nodes {1} nps {4} time {5} score mate {0} pv {6}", -(alpha - (alpha / Math.Abs(alpha)) * (max_depth + mate_value + 1)) / 2, Nodecount, current_depth, current_depth + max_ply, (int)(((float)(Nodecount) * 1000) / (sw.ElapsedMilliseconds > 0 ? (float)sw.ElapsedMilliseconds : 1)), (int)(sw.ElapsedMilliseconds), variation_to_string(pv.principalVariation));
            }

            //reset various variables
            last_last_best = last_best;
            last_best = alpha;
            max_ply = 0;

            if (stop)
            {
                stop = false;
                break;
            }
        }

        //reset the nodecount
        Nodecount = 0;

        //stop the stopwatch
        sw.Stop();
        sw.Reset();

        //return the best move
        return Output;
    }
    public pv_out principal_variation_search(position board, int depth, int ply, int alpha, int beta, bool in_check, bool NNUE_avx2, ulong key)
    {
        //define the variables
        bool search_pv = true, gives_check = false, two_fold_repetition = false, improving = true, full_depth_search = false, fail_low = false, pruning_is_safe = false;
        int BestMove = -1;
        reverse_move undo_move = new reverse_move();
        int movecount = 0, interesting_move_count, new_depth;
        int static_score = -illegal_position_value, score = -illegal_position_value, decrease = 0, improvement = 0;
        Accumulator currentacc = null;
        if (NNUE_avx2) currentacc = new Accumulator(16);
        pv_out Output = new pv_out(), current_variation;
        Output.value = alpha;
        TTableEntry entry = new TTableEntry(0, 0, 0, false, false, false);
        move_and_eval_list move_list = new move_and_eval_list();
        int[] played_moves = new int[214];
        int played_move_idx = 0; 

        if (board.fifty_move_rule == 100 || stop)
        {
            Output.value = 0;
            return Output;
        }

        //threefold repetition
        if (is_in_fast_lookup(key))
        {
            if (repetition_count(key) == 2)
            {
                Output.value = 0;
                return Output;
            }
            else if (repetition_count(key) == 1)
                two_fold_repetition = true;
        }

        int KeyValid = IsvalidEntry(key);

        if (KeyValid > -2)
        {
            entry = GetInfoFromEntry(key);

            if (KeyValid == 1)
            {
                if (Math.Abs(entry.Score) >= mate_value)
                    entry.Score -= entry.Score / Math.Abs(entry.Score) * ply;

                //if the position has the right depth return the value of the position
                if (entry.depth >= depth)
                {
                    if (entry.Score >= beta && !entry.fail_low)
                    {
                        Output.value = beta;
                        return Output;
                    }
                    if (entry.Score <= alpha && !entry.fail_high)
                    {
                        Output.value = alpha;
                        return Output;
                    }
                }
            }
        }

        int[] moves = MoveGenerator.legal_move_generator(board, in_check, undo_move, new int[214]);
        int movelist_length = MoveGenerator.move_idx;
        int curr_node_count = Nodecount;
        //copy the accumulator for the current position
        currentacc = chess_stuff.acc_copy(ValueNet.acc);

        AddPositionToLookups(key);

        if (!in_check)
        {
            if (NNUE_avx2)
                static_score = ValueNet.AccToOutput(ValueNet.acc, board.color);
            else
                static_score = eval.pesto_eval(board);
        }

        if (KeyValid == 1)
            score = entry.Score;
        else
            score = static_score;

        /* update the value in the value array
         * if we are in check do not update the value*/

        node_values[ply] = !in_check ? score : illegal_position_value;

        //set the improving flag high if the current value is an improvement
        improvement = ply < 2 ? 0 : node_values[ply] - node_values[ply - 2];
        improving = (improvement > 0 || ply < 2) && !in_check;

        /*we should be able to prune branches only in specific cases
         * when we are not in check
         * and when the depth of the root is larger then 3
         */

        //sort the moves
        move_list = move_order.evaluate_moves(board, moves, movelist_length, ply, false, KeyValid == 1 ? entry.BestMove : BestMove, move_list);
        interesting_move_count = move_order.tactical_move_counter;

        pruning_is_safe = !in_check && root_depth > 3 &&
                chess_stuff.non_mate_window(alpha, beta) &&
                MoveGenerator.non_pawn_material;

        while (move_list.movelist_length > 0)
        {
            movepick current_move = move_order.pick_next_move(move_list);

            //find futile moves
            if (movecount > 2 &&
                current_move.eval < 4000 &&
                pruning_is_safe &&
                depth < 8)
            {
                //late move pruning
                if (movecount >= move_pruning(depth, improving) - interesting_move_count)
                    break;

                //if lmr is allowed
                if (depth > 2 && movecount > interesting_move_count) 
                {
                    //calculate tthe depth of the late move reduction
                    int lmr_depth = depth - (1 + reduction(depth, movecount - interesting_move_count, true));

                    //futility pruning
                    if (static_score + extended_futility_pruning_margin(lmr_depth, true) < alpha)
                        break;
                    //history pruning
                    if (current_move.eval < history_pruning_margin(lmr_depth, improving))
                        break;
                }
            }

            movecount++;

            current_variation = new pv_out();
            move_order.add_current_move(current_move.move, board, ply);

            //play the move
            board = make_move(board, current_move.move, NNUE_avx2, undo_move);

            ulong new_key = hash.update_position_hash_after_move(board, undo_move, key);

            //calculate if the current move gives check
            gives_check = MoveGenerator.fast_check(board, current_move.move);

            //set the new depth
            new_depth = depth - 1;

            /*calculate depth extentions*/
            
            //check extention
            if (in_check)
                new_depth++;

            //internal iterative reductiond by Ed Schröder
            else if (depth >= 8 && KeyValid != 1)
                new_depth--;

            //if the current depth is 1 do a quiescent search
            if (new_depth <= 0)
            {
                current_variation.value = -quiescence_search(board, -beta, -alpha, NNUE_avx2, 0, ply + 1, new_key, gives_check);
                current_variation.principalVariation.Add(current_move.move);
            }
            //else just call the function recursively
            else
            {
                if (search_pv)
                {
                    current_variation = principal_variation_search(board, new_depth, ply + 1, -beta, -alpha, gives_check, NNUE_avx2, new_key);
                    current_variation.value = -current_variation.value;
                    current_variation.principalVariation.Insert(0, current_move.move);
                }
                else
                {
                    //late move reduction
                    if (depth > 2 && movecount > 1 && movecount > interesting_move_count && !in_check && !gives_check)
                    {
                        decrease = reduction(new_depth, movecount - interesting_move_count, true);

                        //if we are improving or not decrease more or less
                        //decrease -= Math.Max(Math.Min(improvement / 500, 2), -2);
                        if (!improving) decrease += 1;
                        //if the king is in check and the king moves
                        /*if (in_check && (board[current_move.move[2], current_move.move[3]] & 0b00001110) == 0b110)
                            decrease -= 1;

                        if (gives_check)
                            decrease -= 1;*/

                        //at least a counter move
                        if (current_move.eval >= 4000)
                            decrease -= 1;

                        //normal quiet move reduction
                        decrease -= (int)Math.Max(Math.Min(current_move.eval / 500, 2), -2);

                        int lmr_depth = Math.Max(Math.Min(new_depth, new_depth - decrease), 1);

                        current_variation.value = -zero_window_search(board, lmr_depth, ply + 1, -(alpha + 1), -alpha, gives_check, NNUE_avx2, new_key);

                        if (current_variation.value > alpha && lmr_depth < new_depth && current_variation.value != illegal_position_value) 
                            full_depth_search = true;
                    }
                    else
                        full_depth_search = true;

                    if (full_depth_search)
                    {
                        current_variation.value = -zero_window_search(board, new_depth, ply + 1, -(alpha + 1), -alpha, gives_check, NNUE_avx2, new_key);

                        full_depth_search = false;
                    }

                    if (current_variation.value > alpha && current_variation.value < beta)
                    {
                        current_variation = principal_variation_search(board, new_depth, ply + 1, -beta, -alpha, gives_check, NNUE_avx2, new_key);
                        current_variation.value = -current_variation.value;
                        current_variation.principalVariation.Insert(0, current_move.move);
                    }
                }
            }

            //undo the move
            board = unmake_move(board, undo_move, currentacc, NNUE_avx2);
            played_moves[played_move_idx] = current_move.move;
            played_move_idx++;

            if (current_variation.value > alpha)
            {
                alpha = current_variation.value;
                BestMove = current_move.move;
                Output = current_variation;
                search_pv = false;

                //if the branch is not better then the currently best branch we can prune the other positions
                if (alpha >= beta)
                {
                    //store the killer move history moves and counter moves
                    move_order.update_histories(board, current_move.move, played_moves, played_move_idx, null_move_pruning, depth, ply, true);

                    //add the best move to the hash table if the current depth is greater than the depth of the entry or thre is no entry in the hash table
                    if (!stop && (KeyValid == -2 || entry.depth <= depth))
                        AddToTable(current_move.move, depth, beta, key, 1, 0);

                    RemovePositionFromLookups(key, !two_fold_repetition);

                    Output.principalVariation = new List<int>();
                    Output.value = beta;
                    return Output;
                }

                //store the killer move history moves and counter moves
                move_order.update_histories(board, current_move.move, played_moves, played_move_idx, null_move_pruning, depth, ply, false);
            }

        }

        RemovePositionFromLookups(key, !two_fold_repetition);

        //if no move was legal return the score for mate
        if (movelist_length == 0)
        {
            //mate
            if (in_check)
            {
                Output.value = -(mate_value + max_depth - ply);
                AddToTable(0, max_depth, Output.value, key, 0, 0);
                Output.principalVariation = new List<int>();
                return Output;
            }
            //stalemate
            else
            {
                Output.value = 0;
                AddToTable(0, max_depth, Output.value, key, 0, 0);
                Output.principalVariation = new List<int>();
                return Output;
            }
        }
        else
        {
            //if we have not managed to exeed alpha we have not found the best move so we use the first move we searched
            if (BestMove == -1)
            {
                fail_low = true;
                BestMove = played_moves[0];
            }

            //add the best move to the hash table if the current depth is greater than the depth of the entry or thre is no entry in the hash table
            if (!stop && (KeyValid == -2 || entry.depth <= depth || !fail_low))
                AddToTable(BestMove, depth, Math.Abs(alpha) > mate_value ? alpha + Math.Abs(alpha) / alpha * ply : alpha, key, 0, (byte)(fail_low ? 1 : 0));

            //return the best score
            return Output;
        }
    }
    public int zero_window_search(position board, int depth, int ply, int alpha, int beta, bool in_check, bool NNUE_avx2, ulong key)
    {
        //define the variables
        int current_score = 0, static_score = -illegal_position_value, score = -illegal_position_value, decrease = 0, improvement = 0;
        byte othercolor = (byte)(board.color ^ 1);
        bool gives_check = false, two_fold_repetition = false, full_depth_search = false;
        int movecount = 0, interesting_move_count, new_depth = 0;
        reverse_move undo_move = new reverse_move();
        bool improving = false, pruning_is_safe = false;
        Accumulator currentacc = null;
        if (NNUE_avx2) currentacc = new Accumulator(16);
        TTableEntry entry = new TTableEntry(0, 0, 0, false, false, false);
        move_and_eval_list move_list = new move_and_eval_list();
        int[] played_moves = new int[214];
        int played_move_idx = 0; 

        if (board.fifty_move_rule == 100 || stop)
            return 0;

        //threefold repetition
        if (is_in_fast_lookup(key))
        {
            if (repetition_count(key) == 2)
                return 0;
            else if (repetition_count(key) == 1)
                two_fold_repetition = true;
        }

        int KeyValid = IsvalidEntry(key);

        if (KeyValid > -2)
        {
            entry = GetInfoFromEntry(key);

            if (KeyValid == 1)
            {
                if (Math.Abs(entry.Score) >= mate_value)
                    entry.Score -= entry.Score / Math.Abs(entry.Score) * ply;
                //if the position has the right depth we can use the value of the position
                if (entry.depth >= depth)
                {
                    //if the score is larger or equal to beta we can return beta
                    if (entry.Score >= beta && !entry.fail_low)
                        return beta;
                    //else if the score is certain and it is smaller then alpha we have an alpha cutoff
                    if (entry.Score <= alpha && !entry.fail_high)
                        return alpha;
                    if (entry.exact)
                        return entry.Score;
                }
            }
        }

        int[] moves = MoveGenerator.legal_move_generator(board, in_check, undo_move, new int[214]);
        int movelist_length = MoveGenerator.move_idx;

        //copy the accumulator for the current position
        currentacc = chess_stuff.acc_copy(ValueNet.acc);

        if (!in_check)
        {
            if (NNUE_avx2)
                static_score = ValueNet.AccToOutput(ValueNet.acc, board.color);
            else
                static_score = eval.pesto_eval(board);
        }

        if (KeyValid == 1)
            score = entry.Score;
        else
            score = static_score;

        /* update the value in the value array
         * if we are in check do not update the value*/

        node_values[ply] = !in_check ? score : illegal_position_value;

        //set the improving flag high if the current value is an improvement
        improvement = ply < 2 ? 0 : node_values[ply] - node_values[ply - 2];
        improving = (improvement > 0 || ply < 2) && !in_check;

        /*we should be able to prune branches only in specific cases
         * when we are not in check
         * and when the depth of the root is larger then 3
         */

        pruning_is_safe = !in_check && root_depth > 3 &&
            chess_stuff.non_mate_window(alpha, beta) &&
            MoveGenerator.non_pawn_material;

        if (pruning_is_safe)
        {
            /*Razoring
             * 
             * if the current score is really bad,
             * we try a quiescence search to look if a tactical sequence can make up for the bad score
             * if this is not the case we just prune 
             */

            if (depth < 4 &&
                score + razoring_margin(depth, improving) < alpha &&
                (KeyValid == 1 && !entry.fail_high))
            {
                int test_value = quiescence_search(board, alpha - 1, alpha, NNUE_avx2, 0, ply + 1, key, in_check);

                if (test_value < alpha)
                    return test_value;
            }

            //Reverse Futility Pruning
            if (depth < 7 &&
                score - reverse_futility_pruning_margin(depth) >= beta &&
                (KeyValid != 1 || !entry.fail_low))
                return beta;

            /* Null Move Pruning
             * 
             * when the position looks to be larger the beta,
             * we look if it is still better when we give the opponent two moves in a row
             * 
             * we want to avoid zugzwang because it is the only case in which 
             * the null move observation (you can always make the evaluation better when you play the best move)
             * does not work, so we do not prune when there are only pawns because there is a larger probability
             * for zugzwang in these positions
             * 
             * else we want to avoid searching two null moves in a row
             */

            if (depth >= 3 &&
                score >= beta &&
                static_score >= beta &&
                !null_move_pruning[ply - 1] &&
                (KeyValid != 1 || !entry.fail_low))
            {
                int nmp_score = 0;

                /* calculate the depth for the null move search:
                 *
                 * 1) the base depth reduction is 2
                 * 2) else the depth gets reduced by a factor 1/6
                 * 3) the larger the delta between the standing pat and beta the more we can reduce
                 */

                int null_move_search_depth = depth - 1 - (3 + depth / 6 + Math.Min(3, (int)(score - beta) / 650));
                
                board = make_null_move(board, undo_move);

                if (null_move_search_depth <= 0)
                    nmp_score = -quiescence_search(board, -beta, -alpha, NNUE_avx2, 0, ply + 1, hash.update_null_move_hash(key, undo_move), false);
                else
                {
                    //add the null move search to the table
                    null_move_pruning[ply] = true;

                    nmp_score = -zero_window_search(board, null_move_search_depth, ply + 1, -beta, -alpha, false, NNUE_avx2, hash.update_null_move_hash(key, undo_move));

                    //remove the null move search from the table
                    null_move_pruning[ply] = false;
                }

                board = MoveGenerator.unmake_move(board, undo_move);

                /*if the new score is better
                 *and the next position is not illegal
                 *and the next value is not mate
                 *return beta
                 */
                if (nmp_score >= beta)
                    return beta;
            }
        }

        //sort the moves
        move_list = move_order.evaluate_moves(board, moves, movelist_length, ply, false, entry.BestMove, move_list);
        interesting_move_count = move_order.tactical_move_counter;

        AddPositionToLookups(key);

        while (move_list.movelist_length > 0)
        {
            movepick current_move = move_order.pick_next_move(move_list);

            //find futile moves
            if (movecount > 2 &&
                current_move.eval < 4000 &&
                pruning_is_safe &&
                depth < 8)
            {
                //late move pruning
                if (movecount >= move_pruning(depth, improving) - interesting_move_count)
                    break;

                //if lmr is allowed
                if (depth > 2 && movecount > interesting_move_count)
                {
                    //calculate tthe depth of the late move reduction
                    int lmr_depth = depth - (1 + reduction(depth, movecount - interesting_move_count, true));

                    //futility pruning
                    if (static_score + extended_futility_pruning_margin(lmr_depth, true) < alpha)
                        break;
                    //history pruning
                    if (current_move.eval < history_pruning_margin(lmr_depth, improving))
                        break;
                }

            }
            movecount++;
            move_order.add_current_move(current_move.move, board, ply);

            //play the move
            board = make_move(board, current_move.move, NNUE_avx2, undo_move);

            ulong new_key = hash.update_position_hash_after_move(board, undo_move, key);

            //calculate if the current move gives check
            gives_check = MoveGenerator.fast_check(board, current_move.move);

            //set the new depth
            new_depth = depth - 1;

            /*calculate depth depth extentions*/

            //check extention
            if (in_check)
                new_depth++;

            //internal iterative reductiond by Ed Schröder
            else if (depth >= 8 && KeyValid != 1)
                new_depth--;

            //if the current depth is 1 do a quiescent search
            if (new_depth <= 0)
                current_score = -quiescence_search(board, -beta, -alpha, NNUE_avx2, 0, ply + 1, new_key, gives_check);

            //else just call the function recursively
            else
            {
                //late move reduction
                if (depth > 2 && movecount > 1 && movecount > interesting_move_count && !in_check && !gives_check)
                {
                    //calculate a base reduction
                    decrease = reduction(new_depth, movecount - interesting_move_count, false);

                    //if we are improving or not decrease more or less
                    //decrease -= Math.Max(Math.Min(improvement / 500, 2), -2);
                    if (!improving) decrease += 1;

                    //if the king is in check and the king moves
                    /*if (in_check && (board[current_move.move[2], current_move.move[3]] & 0b00001110) == 0b110)
                        decrease -= 1;

                    if (gives_check)
                        decrease -= 1;*/

                    //at least a counter move
                    if (current_move.eval >= 4000)
                        decrease -= 1;

                    //normal quiet move reduction
                    else
                        decrease -= (int)Math.Max(Math.Min(current_move.eval / 500, 2), -2);


                    int lmr_depth = Math.Max(Math.Min(new_depth, new_depth - decrease), 1);

                    current_score = -zero_window_search(board, lmr_depth, ply + 1, -beta, -alpha, gives_check, NNUE_avx2, new_key);

                    if (current_score > alpha && lmr_depth < new_depth)
                        full_depth_search = true;
                }
                else
                    full_depth_search = true;

                if (full_depth_search)
                {
                    current_score = -zero_window_search(board, new_depth, ply + 1, -beta, -alpha, gives_check, NNUE_avx2, new_key);

                    full_depth_search = false;
                }
            }

            //undo the move
            board = unmake_move(board, undo_move, currentacc, NNUE_avx2);

            played_moves[played_move_idx] = current_move.move;
            played_move_idx++;

            //if the branch is not better then the currently best branch we can prune the other positions
            if (current_score >= beta)
            {
                //store the killer move history moves and counter moves
                move_order.update_histories(board, current_move.move, played_moves, played_move_idx, null_move_pruning, depth, ply, true);

                //add the best move to the hash table if the current depth is greater than the depth of the entry or thre is no entry in the hash table
                if (!stop && (KeyValid == -2 || entry.depth <= depth))
                    AddToTable(current_move.move, depth, beta, key, 1, 0);

                RemovePositionFromLookups(key, !two_fold_repetition);

                return beta;
            }
        }

        RemovePositionFromLookups(key, !two_fold_repetition);

        //if no move was legal return the score for a terminal node
        if (movelist_length == 0)
        {
            //mate
            if (in_check)
            {
                AddToTable(0, max_depth, -(mate_value + max_depth - ply), key, 0, 0);
                return -(mate_value + max_depth - ply);
            }
            //stalemate
            else
            {
                AddToTable(0, max_depth, 0, key, 0, 0);
                return 0;
            }
        }
        else
        {
            //add the best move to the hash table if there is no entry in the hash table
            if (!stop && (KeyValid == -2 || entry.depth <= depth))
                AddToTable(played_moves[0], depth, Math.Abs(alpha) >= mate_value ? alpha + Math.Abs(alpha) / alpha * ply : alpha, key, 0, 1);

            return alpha;
        }

    }
    public int quiescence_search(position board, int alpha, int beta, bool NNUE_avx2, int depth, int ply, ulong key, bool in_check)
    {
        if (stop)
            return 0;

        //look for repetitions
        if (depth == 0)
        {
            //threefold repetition
            if (is_in_fast_lookup(key) && repetition_count(key) == 2 || board.fifty_move_rule == 100)
                return 0;
        }

        //define the variables
        Nodecount++;
        int standing_pat = -illegal_position_value, current_score = 0, BestMove = 0, move_count = 0;
        byte othercolor = (byte)(board.color ^ 1);
        bool fail_low = true, gives_check;
        int[] moves;
        Accumulator currentacc = null;
        if (NNUE_avx2) currentacc = new Accumulator(16);
        move_and_eval_list move_list = new move_and_eval_list();
        TTableEntry entry = new TTableEntry(0, 0, 0, false, false, false);
        int KeyValid = IsvalidEntry(key);
        reverse_move undo_move = new reverse_move();

        if (KeyValid > -2) 
        {
            entry = GetInfoFromEntry(key);

            if (KeyValid == 1)
            {
                if (Math.Abs(entry.Score) >= mate_value)
                    entry.Score -= entry.Score / Math.Abs(entry.Score) * ply;

                //if the score is larger or equal to beta we can return beta
                if (entry.Score >= beta && entry.fail_high)
                    return beta;
                //else if the score is certain and it is smaller then alpha we have an alpha cutoff
                if (entry.Score <= alpha && entry.fail_low)
                    return alpha;

                if (entry.exact)
                    return entry.Score;
            }
        }

        //copy the accumulator for the current position
        currentacc = chess_stuff.acc_copy(ValueNet.acc);

        //if we are in check look for  other moves
        if (in_check)
            moves = MoveGenerator.legal_move_generator(board, in_check, undo_move, new int[214]);
        //else just look for captures
        else
            moves = MoveGenerator.legal_capture_generator(board, in_check, undo_move, new int[100]);

        int movelist_length = MoveGenerator.move_idx;

        //if we are in check standing pat is no allowed because we search all moves and not only captures
        if (!in_check)
        {
            if (NNUE_avx2)
                standing_pat = ValueNet.AccToOutput(ValueNet.acc, board.color);
            else
                standing_pat = eval.pesto_eval(board);
        }

        //if the branch is not better then the currently best branch we can prune the other positions
        if (standing_pat >= beta)
            return beta;

        //delta pruning
        if (standing_pat < alpha - 11000 && !in_check)
            return alpha;

        if (standing_pat > alpha)
            alpha = standing_pat;

        //if the position is quiet return the evaluation
        if (movelist_length == 0)
        {
            max_ply = Math.Max(ply, max_ply);

            //if there is no legal move it is checkmate
            if (in_check) alpha = -(mate_value + max_depth - ply);

            AddToTable(0, 0, alpha, key, 0, 0);

            return alpha;
        }

        //sort the moves
        move_list = move_order.evaluate_moves(board, moves, movelist_length, ply, true, entry.BestMove, move_list);

        while (move_list.movelist_length > 0)
        {
            movepick current_move = move_order.pick_next_move(move_list);

            if (MoveGenerator.non_pawn_material && !in_check && current_move.eval / 3 + standing_pat + 2000 < alpha)
                break;

            move_count++;

            //play the move
            board = make_move(board, current_move.move, NNUE_avx2, undo_move);

            ulong new_key = hash.update_position_hash_after_move(board, undo_move, key);

            //calculate if the current move gives check
            gives_check = MoveGenerator.fast_check(board, current_move.move);

            //calls itself recursively
            current_score = -quiescence_search(board, -beta, -alpha, NNUE_avx2, depth + 1, ply + 1, new_key, gives_check);

            //undo the move
            board = unmake_move(board, undo_move, currentacc, NNUE_avx2);

            //if the current score is not 2 the position is not illegal and therefore we have found a legal move
            if (move_count == 0) BestMove = current_move.move;

            if (current_score > alpha)
            {
                fail_low = false;
                alpha = current_score;
                BestMove = current_move.move;

                //if the branch is not better then the currently best branch we can prune the other positions
                if (current_score >= beta)
                {
                    if (!stop && (KeyValid == -2 || entry.depth == 0))
                        AddToTable(current_move.move, 0, beta, key, 1, 0);
                    return beta;
                }
            }
        }

        //add the best move to the hash table if the current depth is greater than the depth of the entry or thre is no entry in the hash table
        if (!stop && (KeyValid == -2 || entry.depth == 0))
            AddToTable(BestMove, 0, alpha, key, 0, (byte)(fail_low ? 1 : 0));

        //return the best score
        return alpha;
    }
    public alpha_beta_output selfplay_iterative_deepening(position board, int depth, bool NNUE_avx2)
    {
        //initialize the variables
        move_order.reset_movesort();
        int[] movelist = new int[218];
        reverse_move undo_move = new reverse_move();
        stop = false;
        bool position_is_quiet = true;
        byte othercolor = (byte)(board.color ^ 1);
        List<int[]> last_best_moves = new List<int[]>();
        bool search_pv = true, in_check = MoveGenerator.check(board, false), gives_check = false;
        int new_depth = 0;
        movelist = MoveGenerator.legal_move_generator(board, in_check, undo_move, movelist);
        int movelist_length = MoveGenerator.move_idx;
        pv_out current_variation = new pv_out(), pv = new pv_out();
        int alpha = -illegal_position_value, delta_a = 0, delta_b = 0, window_a = 0, window_b = 0, last_best = 0, last_last_best = 0, current_score = 0;
        bool pruning_is_safe = false;
        Accumulator currentacc = null;
        if (NNUE_avx2) currentacc = new Accumulator(16);
        move_and_eval_list move_list = new move_and_eval_list();
        alpha_beta_output Output = new alpha_beta_output();
        time_acces.WaitOne();
        long theoretical_time_usage = time_to_use;
        time_acces.Release();
        //get the key for the position
        ulong key = hash.hash_position(board);

        //start the stopwatch
        sw.Start();
        sw.Start();

        if (NNUE_avx2)
        {
            //the the accumulator position to the starting position
            ValueNet.set_acc_from_position(board);

            //copy the accumulator for the current position
            currentacc = chess_stuff.acc_copy(ValueNet.acc);
            current_score = ValueNet.AccToOutput(ValueNet.acc, board.color);
        }
        else
            current_score = eval.pesto_eval(board);

        pruning_is_safe = !in_check && MoveGenerator.non_pawn_material;

        node_values[0] = !in_check ? current_score : illegal_position_value;

        for (int current_depth = 1; current_depth <= depth; current_depth++)
        {
            depth_acces.WaitOne();
            root_depth = current_depth;
            depth_acces.Release();
            node_values[0] = !in_check ? (IsvalidEntry(key) == 1 ? GetInfoFromEntry(key).Score : current_score) : illegal_position_value;

            if (current_depth >= 4 && Math.Abs(last_last_best) < mate_value)
            {
                //if the current depth is larger then 2 reajust the window
                delta_a = -125;
                window_a = last_last_best + delta_a;
                delta_b = 125;
                window_b = last_last_best + delta_b;
                alpha = window_a;
            }
            else
            {
                window_a = -illegal_position_value;
                window_b = illegal_position_value;
                alpha = window_a;
            }

            while (!stop)
            {
                search_pv = true;

                move_list = move_order.evaluate_moves(board, stuff.copy_int_array(movelist), movelist_length, 0, false, IsvalidEntry(key) == 1 ? GetInfoFromEntry(key).BestMove : 0, move_list);
                while (move_list.movelist_length > 0)
                {
                    movepick current_move = move_order.pick_next_move(move_list);
                    //Debug.Assert(!stuff.int_array_equal(current_move.move, new int[] { 4, 5, 4, 4 }) || current_depth != 3);

                    current_variation = new pv_out();
                    move_order.add_current_move(current_move.move, board, 0);

                    //play the move
                    board = make_move(board, current_move.move, NNUE_avx2, undo_move);

                    //get the hash key
                    ulong new_key = hash.update_position_hash_after_move(board, undo_move, key);

                    //calculate if the current move gives check
                    gives_check = MoveGenerator.fast_check(board, current_move.move);

                    //find if the current position is a terminal position
                    //determining the mate value 2 => not a terminal position , 0 => draw , 1 => mate for white , -1 => mate for black
                    int matingValue = MoveGenerator.is_mate(board, in_check, new reverse_move(), new int[214]);

                    new_depth = current_depth - 1;

                    //checking if the position is not a terminal node
                    if (matingValue != 2)
                    {
                        //if the position is a terminal node the value for the node is set to the mating value from the perspective of the current color
                        current_variation.value = matingValue == 0 ? 0 : mate_value + max_depth;
                        current_variation.principalVariation.Insert(0, current_move.move);
                    }
                    else
                    {
                        //if the current depth is 1 perform a quiescent search
                        if (new_depth <= 0)
                        {
                            current_variation.value = -quiescence_search(board, -window_b, -alpha, NNUE_avx2, 0, 1, new_key, gives_check);

                            current_variation.principalVariation.Add(current_move.move);
                        }
                        //else perform a normal pv search
                        else
                        {
                            //perform a pv search
                            if (search_pv)
                            {
                                current_variation = principal_variation_search(board, new_depth, 1, -window_b, -alpha, gives_check, NNUE_avx2, new_key);
                                current_variation.value = -current_variation.value;
                                current_variation.principalVariation.Insert(0, current_move.move);
                            }
                            else
                            {

                                current_variation.value = -zero_window_search(board, new_depth, 1, -(alpha + 1), -alpha, gives_check, NNUE_avx2, new_key);

                                if (stop)
                                {
                                    //undo the move
                                    board = unmake_move(board, undo_move, currentacc, NNUE_avx2);
                                    break;
                                }

                                if (current_variation.value > alpha && current_variation.value < window_b)
                                {
                                    current_variation = principal_variation_search(board, new_depth, 1, -window_b, -alpha, gives_check, NNUE_avx2, new_key);
                                    current_variation.value = -current_variation.value;
                                    current_variation.principalVariation.Insert(0, current_move.move);
                                }
                            }
                        }
                    }

                    //undo the move
                    board = unmake_move(board, undo_move, currentacc, NNUE_avx2);

                    //determine if the current move is better than the currently best move only if it is 
                    if (alpha < current_variation.value)
                    {
                        if (chess_stuff.is_capture(current_move.move, board))
                            move_order.update_history_move(board, current_move.move, 0, 0, 0, 0, Math.Min((float)(depth * depth) / 10f, 40), 0);
                        else
                            move_order.update_chistory_move(board, current_move.move, Math.Min((float)(depth * depth) / 10f, 40));

                        alpha = current_variation.value;
                        pv = current_variation;
                        search_pv = false;
                    }

                    if (stop || alpha >= window_b)
                        break;
                }

                if (alpha <= window_a)
                {
                    delta_a *= 2;
                    window_a = last_last_best + delta_a;
                    alpha = window_a;
                }
                else if (alpha >= window_b)
                {
                    delta_b *= 2;
                    alpha = window_a;
                    window_b = last_last_best + delta_b;
                }
                else
                    break;
            }


            AddToTable(pv.principalVariation[0], current_depth, alpha, key, 0, 0);

            //reset various variables
            last_last_best = last_best;
            last_best = alpha;
            max_ply = 0;
        }

        //reset the nodecount
        Nodecount = 0;

        Output.Score = chess_stuff.convert_millipawn_to_wdl(pv.value);
        if (Math.Abs(Output.Score) != 1)
        {
            if (chess_stuff.is_capture(pv.principalVariation[0], board))
                position_is_quiet = false;
            Output.movelist.Add(pv.principalVariation[0]);
        }

        Output.is_quiet = position_is_quiet;

        //reset the nodecount
        Nodecount = 0;

        return Output;
    }
    public position make_null_move(position board, reverse_move undo_move)
    {
        //change the color
        board.color ^= 1;

        //en passent
        undo_move.en_passent = board.en_passent_square;
        board.en_passent_square = byte.MaxValue;

        //fifty move rule
        undo_move.fifty_move_rule = board.fifty_move_rule;

        undo_move.moved_piece_idx = 0;
        undo_move.removed_piece_idx = 0;
        undo_move.king_changes = byte.MaxValue;
        undo_move.rook_changes = byte.MaxValue;

        return board;
    }
    public position make_move(position board, int move, bool use_nnue, reverse_move undo_move)
    { 
        //play the move
        board = MoveGenerator.make_move(board, move, true, undo_move);

        //play the move in the accumulator
        if (use_nnue) ValueNet.update_acc_from_move(board, undo_move);

        return board;
    }
    public position unmake_move(position board, reverse_move inverse_move, Accumulator acc, bool use_nnue)
    {
        //undo the current move
        board = MoveGenerator.unmake_move(board, inverse_move);

        //copy the old accumulator back in the real accumulator
        if (use_nnue) ValueNet.acc = chess_stuff.acc_copy(acc);

        return board;
    }
    public void AddToTable(int Move, int depth, int value, ulong key, byte beta_cutoff, byte alpha_cutoff)
    {
        int index = (int)(key % (ulong)HashTable.GetLength(0));
        //standart logging pattern
        /*
         * depth
         * 
         * then value
         * 
         * then Move
         * 
         * the the  key
         */
        byte[] Log = new byte[8];

        //save the depth and the beta cutoff
        Log[0] = (byte)Math.Min(depth, 127);

        //save the evaluation
        for (int i = 0; i < 4; i++)
            Log[i + 1] = BitConverter.GetBytes(value)[i];

        //save the move
        for (int i = 5; i < 7; i++)
            Log[i] = BitConverter.GetBytes((short)Move + 1)[i - 5];

        //add the flag for the beta cutoff
        Log[7] += (byte)(beta_cutoff << 0);

        //add the flag for the alpha cutoff at the last index of the move
        Log[7] += (byte)(alpha_cutoff << 1);

        byte[] keyArray = BitConverter.GetBytes(key);

        for (int i = 0; i < 16; i++)
            HashTable[index, i] = 0;

        for (int i = 0; i < 8; i++)
            HashTable[index, i] = Log[i];

        for (int i = 8; i < keyArray.Length + 8; i++)
            HashTable[index, i] = keyArray[i - 8];
    }
    public int IsvalidEntry(ulong key)
    {
        int index = (int)(key % (ulong)HashTable.GetLength(0));
        if (HashTable[index, 0] != 0)
        {
            byte[] values = new byte[8];
            for (int i = 0; i < 8; i++)
                values[i] = HashTable[index, 8 + i];

            ulong otherkey = BitConverter.ToUInt64(values);

            if (otherkey == key)
                return 1;
            else
                return -1;
        }
        else
        {
            return -2;
        }
    }
    public TTableEntry GetInfoFromEntry(ulong key)
    {
        int index = (int)(key % (ulong)HashTable.GetLength(0));
        byte depth = HashTable[index, 0];
        byte[] EvalParts = new byte[4];
        byte[] move_parts = new byte[2];
        EvalParts[0] = HashTable[index, 1];
        EvalParts[1] = HashTable[index, 2];
        EvalParts[2] = HashTable[index, 3];
        EvalParts[3] = HashTable[index, 4];
        int eval = BitConverter.ToInt32(EvalParts);
        int Movesize = 5;

        //get the flag for the beta cutoff
        bool beta_cutoff = (HashTable[index, 7] & 0b01) != 0;

        //get the flag for the alpha cutoff
        bool alpha_cutoff = (HashTable[index, 7] & 0b10) != 0;

        //collect the move
        if (HashTable[index, 9] == 0)
            Movesize--;

        for (int i = 5; i < 7; i++)
            move_parts[i - 5] = HashTable[index, i];

        int Move = BitConverter.ToInt16(move_parts) - 1;

        return new TTableEntry(Move, eval, depth, beta_cutoff, alpha_cutoff, !beta_cutoff && !alpha_cutoff);
    }
  
    public void AddPositionToLookups(ulong key)
    {
        //add to fast lookup
        repetion_lookup[key % (ulong)repetion_lookup.Length] = true;

        //add to move array
        repetitions[move_counter] = key;

        move_counter++;
    }
    public bool is_in_fast_lookup(ulong key)
    {
        return repetion_lookup[key % (ulong)repetion_lookup.Length];
    }
    public int repetition_count(ulong key)
    {
        int count = 0;
        for (int i = 0; i <= move_counter; i++)
        {
            if (repetitions[i] == key)
                count++;
        }
        return count;
    }
    public position PlayGameFromMoves(position board, int[] moves)
    {
        board.fifty_move_rule = 0;
        move_counter = 0;
        ulong key = 0;
        int[] pseudolegal_movelist = new int[218];
        key = hash.hash_position(board);

        AddPositionToLookups(key);

        for (int i = 0; i < moves.Length; i++)
        {
            int move = moves[i];
            byte other = (byte)(move >> 12);
            if (other == 0)
            {
                pseudolegal_movelist = MoveGenerator.generate_movelist(board, pseudolegal_movelist);
                int movelist_length = MoveGenerator.move_idx;
                for (int j = 0; j < movelist_length; j++)
                    if ((pseudolegal_movelist[j] & 0b0000111111111111) == move)
                        move = pseudolegal_movelist[j];
            }

            board = play_move(board, move, false, null);
        }


        return board;
    }
    public position play_move(position board, int Move, bool use_reverse_move, reverse_move undo_move)
    {
        board = MoveGenerator.make_move(board, Move, use_reverse_move, undo_move);

        if (board.fifty_move_rule == 0)
            reset_lookups();

        ulong key = hash.hash_position(board);

        AddPositionToLookups(key);

        return board;
    }
    public void reset_lookups()
    {
        move_counter = 0;
        repetion_lookup = new bool[ushort.MaxValue];
    }
    public void RemovePositionFromLookups(ulong key, bool both)
    {
        //remove from fast lookup
        if (both)
            repetion_lookup[key % (ulong)repetion_lookup.Length] = true;

        //derease the move counter
        move_counter--;
    }
    public int reduction_a(int depth, int movecount, bool pv_node)
    {
        double multiplier = 1;
        if (pv_node) multiplier = 2 / 3;

        return (byte)(multiplier * (Math.Sqrt(depth - 1) + Math.Sqrt(movecount - 1)));
    }
    public void reduction_b()
    {
        int reduction = 0;

        for (int depth = 2; depth < 64; depth++)
        {
            for (int movecount = 3; movecount < 64; movecount++)
            {
                reduction = (int)Math.Sqrt((double)((depth - 2) * (movecount - 3) / 12));

                if (movecount <= 4)
                    reduction = Math.Min(reduction, 1);

                move_reductions[1, depth, movecount] = reduction;
                move_reductions[0, depth, movecount] = Math.Max(reduction - 1, 0);
            }
        }
    }
    public int reduction(int depth, int movecount, bool pv_node)
    {
        return move_reductions[pv_node ? 0 : 1, Math.Min(depth, 63), Math.Min(movecount, 63)];
    }
    public void init_reductions(int min_depth_pv, int min_depth, int min_moves_pv, int min_moves, double pv_divisor, double divisor, double pv_add, double add)
    {
        for (int depth = 0; depth < 64; depth++)
        {
            for (int movecount = 0; movecount < 64; movecount++)
            {
                if (depth > min_depth && movecount > min_moves) move_reductions[1, depth, movecount] = (byte)(Math.Log(depth) * Math.Log(movecount) / divisor + add);
                if (depth > min_depth_pv && movecount > min_moves_pv) move_reductions[0, depth, movecount] = (byte)(Math.Log(depth) * Math.Log(movecount) / pv_divisor + pv_add);
            }
        }
    }
    public int razoring_margin(int depth, bool improving)
    {
        return 2000 * (depth * depth + (improving ? 2 : 1));
    }
    public int reverse_futility_pruning_margin(int depth)
    {
        return 1200 * depth;
    }
    public int extended_futility_pruning_margin(int depth, bool pv_node)
    {
        return 1000 * (depth + (pv_node ? 1 : 0)) + 2000;
    }
    public float history_pruning_margin(int lmr_depth, bool improving)
    {
        return -(15 + 20 * lmr_depth * (lmr_depth + (improving ? 1 : 0)));
    }
    public int move_pruning(int depth, bool improving)
    {
        int divisor = improving ? 2 : 3;

        return 6 + 18 * depth * depth / divisor;
    }
    public string variation_to_string(List<int> variation)
    {
        string Output = "";
        foreach (int move in variation)
            Output += chess_stuff.move_to_string(move) + " ";
        return Output;
    }
    public void display_sorting()
    {
        int complete_count = 0;
        foreach (int count in sorting_counter)
            complete_count += count;
        for (int i = 0; i < 10; i++)
            Console.WriteLine("the count at {0} made up for {1}% of the {2} entries", i + 1, (sorting_counter[i] * 100) / complete_count, complete_count);
        sorting_counter = new int[300];
    }
}

class TTableEntry
{
    public int BestMove;
    public int Score;
    public byte depth;
    public bool fail_high = false, fail_low = false, exact = false;
    public TTableEntry(int Bestmove, int CurrentScore, byte Currentdepth, bool cut_node, bool all_node, bool pv_node)
    {
        BestMove = Bestmove;
        Score = CurrentScore;
        depth = Currentdepth;
        fail_high = cut_node;
        fail_low = all_node;
        exact = pv_node;
    }
}
class RepetitionTable
{
    public long Key = 0;
    public RepetitionTable(long key)
    {
        Key = key;
    }
}
class alpha_beta_output
{
    public bool is_quiet = false;
    public bool draw = false;
    public List<int> movelist = new List<int>();
    public float Score;
}
class pv_out
{
    public int value = -80000;
    public List<int> principalVariation = new List<int>();
}

