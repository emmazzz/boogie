procedure A({:linear "tid"} i': int) returns ({:linear "tid"} i: int);
  ensures i == i';

procedure{:entrypoint} B({:linear "tid"} i': int) returns ({:linear "tid"} i: int) 
{
  i := i';
  call i := A(i);
  assert false;
}

